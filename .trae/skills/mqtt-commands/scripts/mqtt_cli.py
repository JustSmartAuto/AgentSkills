# -*- coding: utf-8 -*-
"""向安全带检测软件发送 MQTT 控制指令并打印 JSON 回执。

用法：
  python mqtt_cli.py --host 192.168.1.34 --cmd get_user
  python mqtt_cli.py --host 192.168.1.34 --cmd login --arg user=Admin --arg password=123456
  python mqtt_cli.py --host 192.168.1.34 --cmd force_ok --arg enable=true
  python mqtt_cli.py --host 192.168.1.34 --json '{"cmd":"manual_trigger"}'

依赖：paho-mqtt（本机与工控机已安装）。
"""

import argparse
import json
import sys
import threading
import uuid

import paho.mqtt.client as mqtt

DEFAULT_TOPIC = "/SeatBeltInspection/cli/raw"
DEFAULT_RESULT_TOPIC = "/SeatBeltInspection/cli/result"


def parse_args():
    parser = argparse.ArgumentParser(description="SeatBeltInspection MQTT 指令客户端")
    parser.add_argument("--host", default="127.0.0.1", help="Broker 地址（工控机本机用 127.0.0.1）")
    parser.add_argument("--port", type=int, default=8907)
    parser.add_argument("--topic", default=DEFAULT_TOPIC, help="请求主题")
    parser.add_argument("--result-topic", default=DEFAULT_RESULT_TOPIC, help="回执主题")
    parser.add_argument("--cmd", help="命令名，如 get_user / login / force_ok")
    parser.add_argument("--arg", action="append", default=[], help="键值参数，可重复：--arg user=Admin")
    parser.add_argument("--json", help="完整 JSON 载荷（优先于 --cmd/--arg）")
    parser.add_argument("--id", default=None, help="请求 id，缺省随机生成")
    parser.add_argument("--wait", type=float, default=10.0, help="等待回执秒数")
    return parser.parse_args()


def build_payload(args):
    if args.json:
        payload = json.loads(args.json)
        payload.setdefault("id", args.id or uuid.uuid4().hex[:8])
        return payload
    if not args.cmd:
        raise SystemExit("必须提供 --cmd 或 --json")
    payload = {"id": args.id or uuid.uuid4().hex[:8], "cmd": args.cmd}
    for item in args.arg:
        if "=" not in item:
            raise SystemExit("--arg 需要 k=v 形式：" + item)
        key, value = item.split("=", 1)
        lowered = value.strip().lower()
        if lowered in ("true", "false"):
            payload[key] = lowered == "true"
        else:
            payload[key] = value
    return payload


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    args = parse_args()
    payload = build_payload(args)

    received = {}
    done = threading.Event()

    try:
        client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2)
    except AttributeError:
        client = mqtt.Client()

    def on_connect(client, userdata, flags, *rest):
        client.subscribe(args.result_topic)

    def on_message(client, userdata, message):
        try:
            data = json.loads(message.payload.decode("utf-8"))
        except Exception:
            data = {"raw": message.payload.decode("utf-8", "replace")}
        if str(data.get("Id", "")) == str(payload.get("id")):
            received.update(data)
            done.set()

    client.on_connect = on_connect
    client.on_message = on_message
    client.connect(args.host, args.port, 10)
    client.loop_start()
    try:
        client.subscribe(args.result_topic)
        client.publish(args.topic, json.dumps(payload, ensure_ascii=False))
        done.wait(args.wait)
    finally:
        client.loop_stop()
        client.disconnect()

    print("请求: " + json.dumps(payload, ensure_ascii=False))
    if received:
        print("回执: " + json.dumps(received, ensure_ascii=False, indent=2))
        sys.exit(0 if received.get("Success") else 2)
    print("回执: <超时，%.0f 秒内未收到 id=%s 的响应>" % (args.wait, payload.get("id")))
    sys.exit(1)


if __name__ == "__main__":
    main()
