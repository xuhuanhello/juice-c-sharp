#!/usr/bin/env python3
"""签发一个 coturn use-auth-secret（时限 HMAC）凭据。

决议：https://github.com/xuhuanhello/juice-c-sharp/issues/117

两个用途：
  1. 手工验证 / 排查 —— 签一个出来喂 turnutils_uclient。
  2. **#121 服务端签发逻辑的参照实现** —— 那边照这个形状写即可，
     信令服务器在收到 create-room / join-room 时算一遍，把结果填进
     #116 定好的 iceServers 字段。

用法：
    python3 mint-credential.py --secret <TURN_SECRET> [--name demo] [--ttl 43200]
    python3 mint-credential.py --secret <TURN_SECRET> --json
"""

import argparse
import base64
import hashlib
import hmac
import json
import sys
import time


def mint(secret: str, name: str = "demo", ttl: int = 43200):
    """返回 (username, credential)。

    公式（coturn use-auth-secret / TURN REST API）：
        username   = "<unix 过期时间戳>:<任意名字>"
        credential = base64( HMAC-SHA1( secret, username ) )

    注意 username 里那个数字是**绝对过期时刻**，不是时长 —— 写成时长
    会让 coturn 认为凭据早就过期了，表现为 401。
    """
    expiry = int(time.time()) + ttl
    username = f"{expiry}:{name}"
    digest = hmac.new(
        secret.encode("utf-8"), username.encode("utf-8"), hashlib.sha1
    ).digest()
    credential = base64.b64encode(digest).decode("ascii")
    return username, credential


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--secret", required=True, help="与 coturn static-auth-secret 逐字节一致")
    p.add_argument("--name", default="demo", help="username 的后半段，任意（默认 demo）")
    p.add_argument("--ttl", type=int, default=43200, help="有效秒数（默认 43200 = 12 小时）")
    p.add_argument("--json", action="store_true", help="按 iceServers 形状输出")
    p.add_argument("--urls", default="turn:turn.example.org:3478?transport=udp",
                   help="--json 时填进 urls 的值，逗号分隔")
    args = p.parse_args()

    if args.ttl <= 0:
        print("ttl 必须为正 —— 负数或零会签出一个已过期的凭据", file=sys.stderr)
        return 2

    username, credential = mint(args.secret, args.name, args.ttl)

    if args.json:
        print(json.dumps([{
            "urls": args.urls.split(","),
            "username": username,
            "credential": credential,
        }], indent=2, ensure_ascii=False))
    else:
        print(f"username:   {username}")
        print(f"credential: {credential}")
        print(f"（{args.ttl} 秒后过期）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
