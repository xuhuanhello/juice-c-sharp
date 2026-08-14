# deploy —— 示例的配套设施

datachannel-unity FishNet 示例要用的两台服务：**TURN/STUN** 与**信令服务器**。

这**不是包的一部分**。`docs/SPEC.md` 第 27 行把信令定为 application-supplied、第 36 行把 TURN 服务器列在包的 out of scope。别把这里的东西写进 SPEC 的规范章节。

决议：[信令协议 #116](https://github.com/xuhuanhello/juice-c-sharp/issues/116) · [TURN 凭据 #117](https://github.com/xuhuanhello/juice-c-sharp/issues/117) · [本布局 #122](https://github.com/xuhuanhello/juice-c-sharp/issues/122)

```
deploy/
├── docker-compose.yml     两个 service：turn 与 signal
├── .env.example           复制成 .env 填几个值；.env 不入库
├── turn/
│   ├── turnserver.conf    只放非密钥项，因此入库
│   └── mint-credential.py 签发凭据；也是信令服务器签发逻辑的**唯一来源**
└── signal/
    ├── Dockerfile         build context 是 deploy/ 而非 signal/，见下
    ├── smoke.py           16 条协议冒烟，退出码即判定
    └── src/server.py      #116 那 10 种消息 + 一张房间表
```

**凭据公式只存在一份。** `signal/src/server.py` 按路径加载 `turn/mint-credential.py` 里的 `mint()`，不自己实现一遍 —— 这也是 `signal/Dockerfile` 的 build context 要设成 `deploy/` 的原因（镜像得拿到 `turn/` 那个文件）。抄两遍就会有对不上的那天，而症状是客户端一片莫名的 401。

**已在本机验过**（2026-08-14，`websockets==12.0`）：直接跑 16/16、Docker 容器里 16/16、自签证书走 wss 16/16；不带 `--insecure` 时在证书校验上失败 —— 那个反例确认 TLS 真在校验，不是静默退回明文。**没有在公网 VPS 上验过**，那是本 ticket 交给部署者的部分。

## 部署（Linux VPS）

需要 Docker 与 docker compose。

```bash
git clone <repo> && cd juice-c-sharp/deploy
cp .env.example .env
openssl rand -hex 32        # 输出填进 .env 的 TURN_SECRET
```

`.env` 里要填三个（其余可留默认）：

| 值 | 填什么 |
|----|--------|
| `TURN_SECRET` | 上面 `openssl rand -hex 32` 的输出 |
| `TURN_REALM` | 你的域名，例如 `turn.你的域名` |
| `TURN_EXTERNAL_IP` | **公网 IP。云主机必填** |

`TURN_EXTERNAL_IP` 是最常见的翻车点：云主机网卡上是私网地址，不填这条 coturn 会把私网地址当 relay 候选发出去，对端永远连不上。

把 `turn/turnserver.conf` 里的 `realm` 改成和 `.env` 一致，然后：

```bash
docker compose up -d turn
docker compose logs -f turn      # 看到 "Relay ports initialization done" 即起来了
```

### 防火墙 / 安全组

| 端口 | 协议 | 用途 |
|------|------|------|
| 3478 | UDP + TCP | STUN/TURN |
| 49160–49200 | UDP | relay 分配段（与 `turnserver.conf` 的 `min-port`/`max-port` 一致）|

端口段开小是有意的：`network_mode: host` 下逐个映射不现实，40 个够示例用。要更多就同时改 conf 和安全组。

## 部署信令服务器

协议由 [#116](https://github.com/xuhuanhello/juice-c-sharp/issues/116) 定，实现见 `signal/src/server.py`。它是**哑中继加一张房间表** —— 路由消息、发身份、报离开，从不解析 `payload`、从不碰 SDP。

`.env` 里信令这半边要填的：

| 值 | 填什么 |
|----|--------|
| `TURN_URLS` / `STUN_URLS` | 把 `turn.example.org` 换成你的域名。**客户端拿到的就是这两行的值** |
| `SIGNAL_PORT` | 宿主上暴露哪个端口，默认 8080。容器内恒为 8080 |
| `SIGNAL_TLS_*` | 见下一节 |

`TURN_SECRET` 不用再填一遍 —— 信令和 coturn **共用那一个值**，这正是两者收在同一个 `deploy/` 的理由。

### TLS 放哪：两条路，二选一

[#116](https://github.com/xuhuanhello/juice-c-sharp/issues/116) 选 wss 是为了买断 Android cleartext 与 iOS ATS 的不确定性。那**只要求客户端侧看到的是 `wss://`**，不管 TLS 在哪一层终止 —— 所以两条路都行。

**① 已经在跑 nginx / Caddy** → `.env` 里 `SIGNAL_TLS_*` **三项全部留空**，容器监听明文 ws，由反代对外提供 wss。

Caddy 两行就够：

```caddyfile
signal.你的域名 {
    reverse_proxy 127.0.0.1:8080
}
```

nginx 要显式带上 WebSocket 那两个头，**漏了会握手失败**：

```nginx
location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;      # 这两行是 WebSocket 必需
    proxy_set_header Connection "upgrade";
    proxy_read_timeout 3600s;                    # 信令连接常驻，默认 60s 会被掐断
}
```

`proxy_read_timeout` 那条要注意：[#116](https://github.com/xuhuanhello/juice-c-sharp/issues/116) 定的是**信令连接常驻**（等下一个玩家、当带外通道），nginx 默认 60 秒无数据就断，而 host 在等人进房时正是长时间无数据。

**② 让容器自己终止 TLS** → 填三项：

```bash
SIGNAL_TLS_DIR=/etc/letsencrypt/live/signal.你的域名   # 宿主路径，会挂到容器 /certs
SIGNAL_TLS_CERT=/certs/fullchain.pem                   # 容器内路径
SIGNAL_TLS_KEY=/certs/privkey.pem                      # 容器内路径
```

用 `fullchain.pem` 而不是 `cert.pem` —— 少了中间证书，桌面浏览器往往还能连，而移动端会拒。

certbot 续期后要 `docker compose restart signal`：证书是启动时读进内存的，文件换了不会自动生效。

### 起

```bash
docker compose up -d signal
docker compose logs -f signal
```

看到这行就对了：

```
信令服务器已监听 0.0.0.0:8080（wss）
```

括号里是 `ws` 还是 `wss` 直接告诉你走的哪条路。走反代那条路时这里应当是 `ws`，并且**上面会有一条 WARNING** 提醒你客户端必须经反代访问 —— 那条警告是刻意的，它让「以为配了 TLS 其实没配」这件事无法静默发生。

防火墙除 TURN 那两项外，再开：

| 端口 | 协议 | 用途 |
|------|------|------|
| 443 | TCP | wss（走反代，或容器直接监听 443）|
| `SIGNAL_PORT` | TCP | 仅当你把容器端口直接暴露到公网时 |

走反代的话 `SIGNAL_PORT` **不要**对公网开 —— 让它只在 localhost 上被反代访问。

### 冒烟：`signal/smoke.py`

这一步是 [#121](https://github.com/xuhuanhello/juice-c-sharp/issues/121) 的验收条件，**别跳**。它不需要 Unity，在你本机跑就行：

```bash
python3 -m pip install "websockets==12.0"
python3 signal/smoke.py wss://signal.你的域名
```

16 条检查，**退出码即判定**（0 全过）。它验的不只是「能连上」：

- `from` 由服务器盖章 —— 脚本故意自报一个假 `from`，检查被覆盖
- **per-sender FIFO** —— 连发 description + 3 条 candidate，检查到达顺序一致。这条是 #116 的硬约束 1，顺序错了在真客户端上表现成上游抛 `Got a remote candidate without remote description`，而那看起来像「偶发连不上」
- SDP 原样透传 —— 塞一个带 `\r\n` 和中文的 SDP 逐字节比对
- `iceServers` 的形状能直接反序列化成 C# 的 `IceServer`，且 TURN 的 `username` 是 `<过期时间戳>:<名字>`
- 房间码大小写不敏感、不存在的码报 `no-such-room`
- host 断开 → 剩余 client 收到 `room-closed`；client 断开 → host 收到 `peer-left`；房间真的销毁

自签证书加 `--insecure`。**但正式部署不要用 `--insecure` 跑** —— 那会把「证书链配错」这件事一起跳过。

## 验证中继

签一个凭据：

```bash
python3 turn/mint-credential.py --secret "$(grep TURN_SECRET .env | cut -d= -f2)" --ttl 600
```

拿它打一遍（在服务器上跑，`<公网IP>` 换成你的）：

```bash
docker compose exec turn turnutils_uclient -T -u <username> -w <credential> \
  -e <公网IP> -n 5 -m 1 <公网IP>
```

**判据**：输出里有 `Total transmit time` 且 `Total lost packets 0` = 中继成功。出现 `error 401` = 凭据没过（先核对 secret 与时钟）。

想确认认证真的在生效，用一个错的 credential 再打一次 —— **必须失败**。只看成功那一次不构成证据。

## 本机试跑（macOS）

`network_mode: host` 在 Docker Desktop for Mac/Windows **不可用**。本机只想验凭据逻辑的话，直接跑容器、在容器内自打自收：

```bash
docker run --rm -e TURN_SECRET=testsecret coturn/coturn:4.7.0 \
  turnserver --use-auth-secret --static-auth-secret=testsecret \
  --realm=test.local --listening-ip=127.0.0.1 --allow-loopback-peers \
  --no-tls --no-dtls --no-cli
```

`--allow-loopback-peers` **只用于本机验证** —— coturn 自己会警告 `opens a possible security vulnerability`。生产配置里绝不能有。

## 排查

| 症状 | 先看 |
|------|------|
| relay 候选是私网地址 | `TURN_EXTERNAL_IP` 没填 |
| 一片 401 / 438 | ① `TURN_SECRET` 两处不一致（复制时带了空格）② **两台服务器时钟不同步** —— username 里是 unix 过期时间戳 |
| `403 Forbidden IP` | 中继目标被拒（loopback / 多播）。真机之间不会遇到 |
| 认证过了但连不上 | 防火墙没放行 UDP 端口段 |

## 部署完之后：回填这张表

[#121](https://github.com/xuhuanhello/juice-c-sharp/issues/121) 的验收要求把这些事实记下来，后续 ticket 依赖它们。把下面这块整段贴到 #121 的评论里，填空即可。

**只填 URL 与端口，不要贴 `TURN_SECRET`、不要贴证书私钥** —— 那两样贴出来就等于泄漏。`.env` 已被 gitignore，保持那样。

```markdown
## 部署事实（#121）

- **wss URL**：`wss://signal.你的域名`（客户端唯一需要的值）
- **TLS 走哪条路**：容器自终止 / nginx 反代 / Caddy 反代（划掉不适用的）
- **STUN URL**：`stun:你的域名:3478`
- **TURN URL**：`turn:你的域名:3478?transport=udp`、`...transport=tcp`
- **部署在**：<云厂商 + 地域，不用写 IP>
- **怎么重启**：`cd <路径>/deploy && docker compose restart signal`
- **日志在哪**：`docker compose logs -f signal`
- **secret 放在哪**：`<路径>/deploy/.env`（不入库；只写路径，不写值）
- **房间码形态**：6 位，字母表 `23456789ABCDEFGHJKLMNPQRSTUVWXYZ`（无 0/O/1/I），查表大小写不敏感

### smoke.py 结果

<把 `python3 signal/smoke.py wss://…` 的输出整段贴在这里，含末尾那行「N 通过 / N 失败」>

### 已知限制

- 房间表**只在内存里**，重启即清空（示例不做持久化）
- 并发上限没设，也没做限流
- 无鉴权：知道房间码就能进
```

有了 wss URL 我就能接客户端那半边 —— Unity 侧的 `SignalingConfig.json` 只需要这一个值（[#117](https://github.com/xuhuanhello/juice-c-sharp/issues/117) 定的：**客户端一个秘密都没有**）。

## 排查：信令

| 症状 | 看这里 |
|------|--------|
| 客户端连不上，反代日志里是 400 | nginx 漏了 `Upgrade` / `Connection` 两个头 |
| 连上了，等一分钟就断 | 反代的 `proxy_read_timeout` 太短。信令连接常驻，host 等人进房时长时间无数据 |
| 移动端连不上、桌面能连 | 证书链不全，用 `fullchain.pem` 不要用 `cert.pem` |
| 日志里 `（ws）` 而你以为配了 TLS | `SIGNAL_TLS_CERT`/`KEY` 没读到。走反代是对的；不走反代就是配漏了 |
| 客户端拿到凭据但 TURN 报 401 | `TURN_SECRET` 两边不一致 —— 但这在同一份 `.env` 下不该发生。先确认没有第二份 `.env` |
| 容器起不来，日志说 `TURN_SECRET 未设置` | 刻意的：没有 secret 就签不出可用凭据，那会表现成客户端一片莫名的 401，不如当场失败 |

## 凭据轮换

换 `.env` 里的 `TURN_SECRET`，`docker compose up -d --force-recreate turn`，信令服务器一起重启。

**存量凭据全部立即失效**；已建立的连接活到 allocation 到期（Refresh 会被拒）。coturn 的 `static-auth-secret` 是单值，**没有滚动窗口** —— 想新旧共存要走它的数据库模式，示例不值。

泄露时的收场就是这两条命令。这正是 HMAC 相对静态凭据的收益：静态凭据泄露要重出客户端包。
