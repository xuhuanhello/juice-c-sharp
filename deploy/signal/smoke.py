#!/usr/bin/env python3
"""对着一台**已经部署好的**信令服务器跑协议冒烟。

    python3 smoke.py wss://signal.example.org
    python3 smoke.py ws://127.0.0.1:8080          # 本机、未上 TLS
    python3 smoke.py wss://... --insecure         # 自签证书

它验 #121 的验收条件第 4 条（两个客户端进同一房间、能互相收到对方的 description
与 candidate），另外把 #116 的几条硬约束也一起验了：

  1. `from` 由服务器盖章 —— 客户端自报的 from 会被覆盖（硬约束 4）
  2. per-sender FIFO —— 连发 description + 3 条 candidate，检查到达顺序一致
     （硬约束 1；顺序错了在真客户端上表现成上游抛
     `Got a remote candidate without remote description`）
  3. payload 原样透传 —— 塞一个带 \\r\\n 和中文的 SDP，逐字节比对
  4. iceServers 形状能直接喂给 C# 的 IceServer
  5. 房间码大小写不敏感
  6. 不存在的房间码 → error{no-such-room}
  7. host 断开 → 剩余 client 收到 room-closed{host-left}
  8. client 断开 → host 收到 peer-left

**退出码即判定**：0 全过，1 有失败。屏幕上每条都打 PASS/FAIL，可以直接贴到 ticket。
"""

import argparse
import asyncio
import json
import ssl
import sys

import websockets

PASS, FAIL = "PASS", "FAIL"
results = []


def check(name, ok, detail=""):
    results.append((PASS if ok else FAIL, name, detail))
    print(f"  [{PASS if ok else FAIL}] {name}" + (f" — {detail}" if detail else ""))
    return ok


async def recv(ws, want_type=None, timeout=10):
    """收一条，可选地要求 type 匹配。超时算失败而不是挂住。"""
    raw = await asyncio.wait_for(ws.recv(), timeout=timeout)
    msg = json.loads(raw)
    if want_type and msg.get("type") != want_type:
        raise AssertionError(f"期望 {want_type}，收到 {msg.get('type')}：{msg}")
    return msg


async def send(ws, msg_type, payload=None, to=None, frm=None):
    m = {"type": msg_type, "payload": payload or {}}
    if to:
        m["to"] = to
    if frm:
        m["from"] = frm      # 故意自报，用来验服务器会盖掉它
    await ws.send(json.dumps(m, ensure_ascii=False))


def validate_ice(servers, label):
    """iceServers 要能直接反序列化成 C# 的 IceServer（#116）。"""
    if not isinstance(servers, list) or not servers:
        return check(f"{label} 带 iceServers", False, f"不是非空数组：{servers!r}")
    ok = True
    saw_turn = False
    for s in servers:
        if not isinstance(s.get("urls"), list) or not s["urls"]:
            ok = check(f"{label} iceServers[].urls 是非空数组", False, repr(s)) and ok
        for u in s.get("urls", []):
            if u.startswith("turn:"):
                saw_turn = True
                # 时限 HMAC：username 是 "<过期时间戳>:<名字>"（#117）
                un = s.get("username", "")
                if ":" not in un or not un.split(":")[0].isdigit():
                    ok = check(f"{label} TURN username 是 <时间戳>:<名字>", False, repr(un)) and ok
                if not s.get("credential"):
                    ok = check(f"{label} TURN credential 非空", False, "") and ok
    if not saw_turn:
        ok = check(f"{label} 含 turn: 条目", False,
                   "示例硬依赖 TURN，只有 STUN 说明 TURN_URLS 没配（#117）") and ok
    if ok:
        check(f"{label} 的 iceServers 形状可直接喂 IceServer", True,
              f"{len(servers)} 条")
    return ok


# 带 \r\n 与非 ASCII 的 SDP —— #116 硬约束 5：两侧都不解析，原样进出。
# 手搓字符串拼接是这里唯一的翻车位置，所以刻意塞进容易翻车的字符。
TRICKY_SDP = ("v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\n"
              "a=note:换行与中文 both here\r\na=end\r\n")


async def run(url, insecure):
    ssl_ctx = None
    if url.startswith("wss://"):
        ssl_ctx = ssl.create_default_context()
        if insecure:
            ssl_ctx.check_hostname = False
            ssl_ctx.verify_mode = ssl.CERT_NONE

    print(f"\n对 {url} 跑协议冒烟\n")

    print("── 建房与进房 ──")
    host = await websockets.connect(url, ssl=ssl_ctx, open_timeout=15)
    await send(host, "create-room")
    created = await recv(host, "room-created")
    code = created["payload"]["code"]
    host_id = created["payload"]["peerId"]
    check("room-created 带 6 位房间码", len(code) == 6, code)
    check("房间码不含易混字符 0/O/1/I", not (set(code) & set("0O1I")), code)
    check("room-created 的 from 是 server", created.get("from") == "server", str(created.get("from")))
    validate_ice(created["payload"].get("iceServers"), "room-created")

    # 大小写不敏感：故意用小写进房
    client = await websockets.connect(url, ssl=ssl_ctx, open_timeout=15)
    await send(client, "join-room", {"code": code.lower()})
    joined = await recv(client, "joined")
    client_id = joined["payload"]["peerId"]
    check("房间码大小写不敏感", True, f"用 {code.lower()} 进了 {code}")
    check("joined 带显式 hostPeerId", joined["payload"].get("hostPeerId") == host_id,
          f"{joined['payload'].get('hostPeerId')} vs {host_id}")
    validate_ice(joined["payload"].get("iceServers"), "joined")

    print("\n── 转发、盖章、保序 ──")
    # client 连发 description + 3 条 candidate，并**故意自报一个假 from**
    await send(client, "description", {"sdp": TRICKY_SDP, "sdpType": "offer"},
               to=host_id, frm="我是host冒充的")
    for i in range(3):
        await send(client, "candidate", {"candidate": f"candidate:{i} 1 UDP 1 1.2.3.4 100{i} typ host",
                                         "mid": "0"}, to=host_id)

    got = [await recv(host) for _ in range(4)]
    types = [m["type"] for m in got]
    check("per-sender FIFO：description 先到，3 条 candidate 依次在后",
          types == ["description", "candidate", "candidate", "candidate"], str(types))
    check("from 由服务器盖章，不采信客户端自报",
          all(m.get("from") == client_id for m in got),
          f"收到 from={[m.get('from') for m in got]}，期望全是 {client_id}")
    check("SDP 原样透传（含 \\r\\n 与中文）",
          got[0]["payload"]["sdp"] == TRICKY_SDP,
          "逐字节相同" if got[0]["payload"]["sdp"] == TRICKY_SDP else "被改写了")
    check("candidate 顺序与发送一致",
          [m["payload"]["candidate"].split()[0] for m in got[1:]]
          == ["candidate:0", "candidate:1", "candidate:2"], "")

    # 反向：host → client
    await send(host, "description", {"sdp": "v=0\r\na=answer\r\n", "sdpType": "answer"}, to=client_id)
    back = await recv(client, "description")
    check("反向转发（host → client）", back.get("from") == host_id, str(back.get("from")))

    print("\n── 错误与生命周期 ──")
    stray = await websockets.connect(url, ssl=ssl_ctx, open_timeout=15)
    await send(stray, "join-room", {"code": "ZZZZZZ"})
    err = await recv(stray, "error")
    check("不存在的房间码 → error{no-such-room}",
          err["payload"].get("code") == "no-such-room", str(err["payload"]))
    await stray.close()

    # client 断开 → host 收到 peer-left
    await client.close()
    left = await recv(host, "peer-left")
    check("client 断开 → host 收到 peer-left{peerId}",
          left["payload"].get("peerId") == client_id, str(left["payload"]))

    # 再进一个 client，然后关 host → 它该收到 room-closed
    client2 = await websockets.connect(url, ssl=ssl_ctx, open_timeout=15)
    await send(client2, "join-room", {"code": code})
    await recv(client2, "joined")
    await host.close()
    closed = await recv(client2, "room-closed")
    check("host 断开 → 剩余 client 收到 room-closed{host-left}",
          closed["payload"].get("reason") == "host-left", str(closed["payload"]))

    # 房间销毁后原码不该还能进
    client3 = await websockets.connect(url, ssl=ssl_ctx, open_timeout=15)
    await send(client3, "join-room", {"code": code})
    err2 = await recv(client3, "error")
    check("host 走后房间真的销毁了（原码进不去）",
          err2["payload"].get("code") == "no-such-room", str(err2["payload"]))
    await client2.close()
    await client3.close()


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("url", help="wss://… 或 ws://…")
    ap.add_argument("--insecure", action="store_true", help="不校验证书（自签时用）")
    args = ap.parse_args()

    try:
        asyncio.run(run(args.url, args.insecure))
    except Exception as e:
        print(f"\n跑挂了：{type(e).__name__}: {e}")
        results.append((FAIL, "冒烟跑完", str(e)))

    failed = [r for r in results if r[0] == FAIL]
    print(f"\n{'=' * 60}")
    print(f"{len(results) - len(failed)} 通过 / {len(failed)} 失败 / 共 {len(results)} 条")
    if failed:
        print("\n失败项：")
        for _, name, detail in failed:
            print(f"  - {name}" + (f" — {detail}" if detail else ""))
    print("=" * 60)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
