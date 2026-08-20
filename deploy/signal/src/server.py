#!/usr/bin/env python3
"""datachannel-unity FishNet 示例的信令服务器。

协议由 #116 定：WebSocket + JSON 统一信封 + 6 位房间码 + 固定 host + client 当
offerer。**这台服务器是哑中继加一张房间表** —— 它路由消息、发身份、报离开，
从不解析 payload、从不碰 SDP。

  决议：https://github.com/xuhuanhello/juice-c-sharp/issues/116
  凭据：https://github.com/xuhuanhello/juice-c-sharp/issues/117
  本票：https://github.com/xuhuanhello/juice-c-sharp/issues/121

这**不是包的一部分**。SPEC 第 27 行把信令定为 application-supplied，所以这套协议
不写进 docs/SPEC.md 的规范章节。

## 为什么是 asyncio 而不是多 worker

#116 的硬约束 1：**中继必须保证同一发送方的消息保序**（per-sender FIFO）。client
先发 offer 再发 candidate，host 侧若先收到 candidate，上游 processRemoteCandidate
会当场抛 `Got a remote candidate without remote description`。

asyncio 每个连接一个协程、单事件循环顺序处理，天然满足这条。**这也是为什么不能把
房间消息 fan-out 到线程池或无序 pub-sub** —— 那样做会周期性地制造这个异常，而它看
起来像「偶发连不上」。
"""

import asyncio
import importlib.util
import json
import logging
import os
import secrets
import ssl
from dataclasses import dataclass, field

import websockets


def _load_mint():
    """借用 deploy/turn/mint-credential.py 里的 mint()。

    **公式只存在一份，这是刻意的** —— #117 把那个脚本指定为签发逻辑的参照实现，
    在这里抄第二遍就会有两份不一致的那天，而症状是客户端一片莫名的 401。

    按路径加载而不是 import，因为文件名带连字符（那是对的：它同时是个 CLI 工具），
    连字符不是合法模块名。两个候选路径分别对应「容器里」与「本机直接跑」。
    """
    here = os.path.dirname(os.path.abspath(__file__))
    for candidate in (
        os.path.join(here, "turn", "mint-credential.py"),        # 容器：见 Dockerfile
        os.path.join(here, "..", "..", "turn", "mint-credential.py"),  # 本机：deploy/turn/
    ):
        path = os.path.normpath(candidate)
        if not os.path.exists(path):
            continue
        spec = importlib.util.spec_from_file_location("dcu_mint", path)
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        return mod.mint
    raise SystemExit(
        "找不到 mint-credential.py。它是凭据公式的唯一来源（#117），"
        "缺了就签不出凭据 —— 不要在这里重新实现一份。")


mint = _load_mint()

log = logging.getLogger("dcu-signal")

# 房间码字母表：去掉 0/O/1/I。它要被人口述和手输 —— #116 定的。
ROOM_ALPHABET = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"
ROOM_CODE_LEN = 6


@dataclass
class Room:
    code: str
    host_peer_id: str
    host_ws: object
    clients: dict = field(default_factory=dict)  # peerId -> websocket


class Hub:
    """房间表加路由。**不解析 payload。**"""

    def __init__(self, ice_config):
        self._rooms = {}          # 规范化后的 code -> Room
        self._by_ws = {}          # websocket -> (code, peerId, is_host)
        self._peer_seq = 0
        self._ice = ice_config

    # ---- 身份与房间码 --------------------------------------------------

    def _next_peer_id(self, prefix):
        self._peer_seq += 1
        return f"{prefix}{self._peer_seq}"

    def _fresh_code(self):
        # 6 位、去混淆字母表。碰撞就重抽 —— 32^6 约 10 亿，重抽几乎不会发生，
        # 但不写这个循环就会有那天。
        for _ in range(50):
            code = "".join(secrets.choice(ROOM_ALPHABET) for _ in range(ROOM_CODE_LEN))
            if self._norm(code) not in self._rooms:
                return code
        raise RuntimeError("房间码抽不出空位，房间数是不是异常地多？")

    @staticmethod
    def _norm(code):
        """查表大小写不敏感（#116）—— 人手输的东西不该因为大小写连不上。"""
        return (code or "").strip().upper()

    # ---- 发送 ----------------------------------------------------------

    @staticmethod
    async def _send(ws, msg_type, payload, frm="server"):
        """服务器自身发出的消息，from 恒为 "server"（#116 信封表）。"""
        await ws.send(json.dumps(
            {"type": msg_type, "from": frm, "payload": payload},
            ensure_ascii=False))

    @staticmethod
    async def _error(ws, code, message):
        # error.code 取值只有这三种（#116）：no-such-room / room-closed / malformed
        await Hub._send(ws, "error", {"code": code, "message": message})

    # ---- 控制面 --------------------------------------------------------

    async def create_room(self, ws):
        code = self._fresh_code()
        peer_id = self._next_peer_id("h")
        room = Room(code=code, host_peer_id=peer_id, host_ws=ws)
        self._rooms[self._norm(code)] = room
        self._by_ws[ws] = (self._norm(code), peer_id, True)
        log.info("房间 %s 建立，host=%s", code, peer_id)
        await self._send(ws, "room-created", {
            "code": code,
            "peerId": peer_id,
            "iceServers": self._ice.mint(),
        })

    async def join_room(self, ws, payload):
        code = self._norm((payload or {}).get("code"))
        room = self._rooms.get(code)
        if room is None:
            await self._error(ws, "no-such-room", f"房间 {code or '(空)'} 不存在")
            return
        peer_id = self._next_peer_id("c")
        room.clients[peer_id] = ws
        self._by_ws[ws] = (code, peer_id, False)
        log.info("房间 %s：%s 加入（当前 client 数 %d）", room.code, peer_id, len(room.clients))
        await self._send(ws, "joined", {
            "peerId": peer_id,
            "hostPeerId": room.host_peer_id,
            "iceServers": self._ice.mint(),
        })

    # ---- 信令面转发 ----------------------------------------------------

    async def relay(self, ws, msg):
        """转发 description / candidate / reject。

        **只看 `to` 做路由，永不解析 `payload`**（#116）。`from` 由服务器盖章 ——
        不采信上行自报，否则可以冒充别人（硬约束 4）。
        """
        who = self._by_ws.get(ws)
        if who is None:
            await self._error(ws, "malformed", "还没有进房间")
            return
        code, peer_id, is_host = who
        room = self._rooms.get(code)
        if room is None:
            await self._error(ws, "room-closed", "房间已不存在")
            return

        to = msg.get("to")
        target = room.host_ws if to == room.host_peer_id else room.clients.get(to)
        if target is None:
            # 目标不在（可能刚断开）。静默丢 —— 报错也无从补救，而且这条路径在
            # 正常的断开竞态里就会走到。
            log.debug("房间 %s：%s → %s 的 %s 无处可送", room.code, peer_id, to, msg.get("type"))
            return

        # payload 原样透传：不解析、不校验、不重排。SDP 里全是 \r\n，靠 json 转义。
        await target.send(json.dumps({
            "type": msg["type"],
            "from": peer_id,      # 盖章
            "to": to,
            "payload": msg.get("payload") or {},
        }, ensure_ascii=False))

    # ---- 断开 ----------------------------------------------------------

    async def disconnect(self, ws):
        who = self._by_ws.pop(ws, None)
        if who is None:
            return
        code, peer_id, is_host = who
        room = self._rooms.get(code)
        if room is None:
            return

        if is_host:
            # host 走了 → 通知剩余每个 client，销毁房间（#116 状态机）
            log.info("房间 %s：host %s 断开，房间销毁（%d 个 client 收到通知）",
                     room.code, peer_id, len(room.clients))
            for cid, cws in list(room.clients.items()):
                self._by_ws.pop(cws, None)
                try:
                    await self._send(cws, "room-closed", {"reason": "host-left"})
                except Exception:
                    pass  # 对端可能同时断了；通知不到不影响销毁
            self._rooms.pop(code, None)
        else:
            room.clients.pop(peer_id, None)
            log.info("房间 %s：client %s 断开（剩 %d 个）", room.code, peer_id, len(room.clients))
            try:
                # peer-left 是 host 侧回收 connectionId 的触发点（#116 交代给 #120）
                await self._send(room.host_ws, "peer-left", {"peerId": peer_id})
            except Exception:
                pass


class IceConfig:
    """按 #117 的时限 HMAC 签发 iceServers。

    `mint()` 直接来自 deploy/turn/mint-credential.py —— 公式只存在一份。
    """

    def __init__(self, secret, turn_urls, stun_urls, ttl):
        self._secret = secret
        self._turn = [u.strip() for u in (turn_urls or "").split(",") if u.strip()]
        self._stun = [u.strip() for u in (stun_urls or "").split(",") if u.strip()]
        self._ttl = ttl

    def mint(self):
        """每次建房/进房**重新签一份** —— 凭据带绝对过期时刻，复用旧的会过期。"""
        servers = []
        if self._stun:
            servers.append({"urls": self._stun})
        if self._turn:
            username, credential = mint(self._secret, name="dcu", ttl=self._ttl)
            servers.append({
                "urls": self._turn,
                "username": username,
                "credential": credential,
            })
        return servers


# 客户端能发的 10 种消息里，上行只有这几种。其余（room-created / joined /
# peer-left / room-closed / error）是服务器下行的。
CONTROL_UP = {"create-room", "join-room"}
RELAY_UP = {"description", "candidate", "reject"}


async def handle(ws, hub):
    """一个连接一个协程 —— 这就是 per-sender FIFO 的实现（硬约束 1）。

    同一个 socket 的消息在这个循环里顺序处理，不并发、不重排。**别把这里改成
    `asyncio.create_task(...)` 每条消息一个任务** —— 那会让 candidate 有机会
    抢在 description 前面到达对端，制造上游那个 logic_error。
    """
    log.info("连接建入 %s", getattr(ws, "remote_address", "?"))
    try:
        async for raw in ws:
            try:
                msg = json.loads(raw)
            except Exception:
                await hub._error(ws, "malformed", "不是合法 JSON")
                continue
            if not isinstance(msg, dict) or not isinstance(msg.get("type"), str):
                await hub._error(ws, "malformed", "缺 type 字段")
                continue

            t = msg["type"]
            if t == "create-room":
                await hub.create_room(ws)
            elif t == "join-room":
                await hub.join_room(ws, msg.get("payload"))
            elif t in RELAY_UP:
                await hub.relay(ws, msg)
            else:
                # 未知 type 不断连接 —— #116 的信封刻意留了扩展位（host-changed
                # 之类），对未知种类友好比严格更符合那个设计。
                log.debug("忽略未知 type=%s", t)
    except websockets.ConnectionClosed:
        pass
    finally:
        await hub.disconnect(ws)
        log.info("连接断开 %s", getattr(ws, "remote_address", "?"))


def build_ssl():
    """有证书就直接上 wss；没有就明文 ws，由前置反代终止 TLS。

    #116 选 wss 的理由是买断 Android cleartext 与 iOS ATS 的不确定性 —— 那要求
    **客户端看到的是 wss**，至于 TLS 在哪一层终止不影响这一点。所以两条路都留。
    """
    cert = os.environ.get("SIGNAL_TLS_CERT", "").strip()
    key = os.environ.get("SIGNAL_TLS_KEY", "").strip()
    if not cert and not key:
        log.warning("未配置 SIGNAL_TLS_CERT/KEY —— 监听明文 ws。"
                    "客户端必须经前置反代以 wss:// 访问，否则 Android/iOS 可能拦。")
        return None
    if not (cert and key):
        raise SystemExit("SIGNAL_TLS_CERT 与 SIGNAL_TLS_KEY 必须同时给，"
                         "只给一个是配置写漏了，不是一种模式")
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.load_cert_chain(cert, key)
    log.info("已加载证书 %s，监听 wss", cert)
    return ctx


async def main():
    logging.basicConfig(
        level=os.environ.get("SIGNAL_LOG_LEVEL", "INFO").upper(),
        format="%(asctime)s %(levelname)-7s %(message)s")

    secret = os.environ.get("TURN_SECRET", "")
    if not secret:
        # 「让缺失变成失败」（CONTRIBUTING 第一原则）：没有 secret 就签不出可用凭据，
        # 而那会表现成客户端一片莫名的 401 —— 不如现在就停。
        raise SystemExit("TURN_SECRET 未设置。示例硬依赖 TURN，缺它应当当场失败而不是"
                         "签出一堆无效凭据（#117）。")

    ice = IceConfig(
        secret=secret,
        turn_urls=os.environ.get("TURN_URLS", ""),
        stun_urls=os.environ.get("STUN_URLS", ""),
        ttl=int(os.environ.get("TURN_TTL_SECONDS", "43200")),
    )
    hub = Hub(ice)

    host = os.environ.get("SIGNAL_BIND", "0.0.0.0")
    port = int(os.environ.get("SIGNAL_PORT", "8080"))
    ssl_ctx = build_ssl()

    # ping_interval 是协议级 keepalive。#116 明确排除了应用层心跳 —— 靠这个加
    # 客户端的 ClientWebSocket.KeepAliveInterval 就够，自己再写一套只是多一个
    # 会写错的定时器。
    async with websockets.serve(
        lambda ws: handle(ws, hub),
        host, port, ssl=ssl_ctx,
        ping_interval=20, ping_timeout=20, max_size=1 << 20,
    ):
        log.info("信令服务器已监听 %s:%d（%s）", host, port, "wss" if ssl_ctx else "ws")
        await asyncio.Future()   # 常驻


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
