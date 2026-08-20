# 信令服务器（#121，协议见 #116）。
#
# **build context 是 deploy/，不是 deploy/signal/** —— compose 里写的是
#   build: { context: ., dockerfile: signal/Dockerfile }
# 这样镜像里能拿到 turn/mint-credential.py。凭据公式因此只存在一份（#117 指定
# 它为参照实现），而不是在服务端抄第二遍。抄两遍就会有对不上的那天，症状是
# 客户端一片莫名的 401。

FROM python:3.12-slim

# 钉住小版本：websockets 在 12→13 之间改过 handler 签名（从 (ws, path) 变成
# (ws,)），漂上去会直接起不来。
RUN pip install --no-cache-dir "websockets==12.0"

WORKDIR /app
COPY signal/src/ /app/
COPY turn/mint-credential.py /app/turn/mint-credential.py

# 不用 root 跑。它只监听一个端口、不写盘，没有任何需要特权的动作。
RUN useradd --create-home --shell /usr/sbin/nologin dcu \
    && chown -R dcu:dcu /app
USER dcu

# 8080 是容器内端口；对外映射与 TLS 见 docker-compose.yml 与 README。
EXPOSE 8080

# -u 让日志不被缓冲，否则 docker logs 要等缓冲区满才出东西 —— 排查时会以为
# 服务器没反应。
CMD ["python3", "-u", "server.py"]
