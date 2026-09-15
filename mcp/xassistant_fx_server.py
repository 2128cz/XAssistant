#!/usr/bin/env python3
"""XAssistant 效果层的 MCP 垫片：让对话端（AI 客户端）也能触发同一套屏幕效果。

它不实现任何渲染，只做一件事：把 MCP 工具调用翻译成 `XAssistant.exe --fx ...` 的无头命令。
好处是主程序不必常驻一个 IPC 服务，谁都能调 —— 命令行、任务计划、CI、MCP 客户端走的都是同一条路。

对话进行中闪一次条带、对话完成撒一次花，就是把这两个工具挂到客户端的 hook 上：

    xassistant_banner  {"text": "AI接管中", "tone": "warn", "blinks": 2}
    xassistant_confetti {"count": 12}
    xassistant_off     {}

注册（以支持 mcpServers 的客户端为例）：
    {"mcpServers": {"xassistant-fx": {"command": "python", "args": [".../mcp/xassistant_fx_server.py"]}}}
可执行文件位置优先取环境变量 XASSISTANT_EXE，否则在常见输出目录里找。
"""

import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

PROTOCOL_VERSION = "2024-11-05"
SERVER_INFO = {"name": "xassistant-fx", "version": "1.0"}

# 只这三档颜色，与 SlashParser.Vocabulary 的分组一致；改产品那边要同步这里
TONES = ("info", "warn", "error")

TOOLS = [
    {
        "name": "xassistant_banner",
        "description": "在用户屏幕上横一条全屏宽度的斜线警告语（中间一句大字，四边同时亮一圈）。"
                       "用于「我开始动手了 / 需要用户看一眼」这类提示。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "text": {"type": "string", "description": "要显示的一句话，最长 40 字"},
                "tone": {"type": "string", "enum": list(TONES),
                         "description": "info=主题色（默认），warn=黄，error=红"},
                "seconds": {"type": "number", "description": "显示时长秒数；不写用默认 2.4"},
                "blinks": {"type": "integer", "description": "闪烁次数 1-5；不写用 1"},
            },
            "required": ["text"],
        },
    },
    {
        "name": "xassistant_confetti",
        "description": "从屏幕顶部撒一把纸屑。用于「这一轮做完了 / 编译通过」这类庆祝。",
        "inputSchema": {
            "type": "object",
            "properties": {"count": {"type": "integer", "description": "纸屑数量，1-12，默认 12"}},
        },
    },
    {
        "name": "xassistant_off",
        "description": "立刻收起当前那条警告语（用户再敲一个斜杠也是同样的效果）。",
        "inputSchema": {"type": "object", "properties": {}},
    },
]


def find_exe() -> str | None:
    """找 XAssistant.exe：环境变量优先，其次按仓库里几种常见输出目录猜。"""
    env = os.environ.get("XASSISTANT_EXE")
    if env and Path(env).is_file():
        return env
    here = Path(__file__).resolve().parent
    roots = [here.parent, Path.cwd()]
    names = ["XAssistant.exe", "xassistant.exe"]
    for root in roots:
        for guess in ["bin/Debug/net8.0-windows", "bin/Release/net8.0-windows", "."]:
            for name in names:
                candidate = root / guess / name
                if candidate.is_file():
                    return str(candidate)
    return shutil.which("XAssistant")


def run(args: list[str]) -> str:
    """起一个无头实例放效果。不等它退出：动画自己跑完自己退，卡在这儿只会拖慢对话。"""
    exe = find_exe()
    if not exe:
        return "找不到 XAssistant.exe（可设环境变量 XASSISTANT_EXE 指向它）"
    try:
        subprocess.Popen([exe, "--fx", *args], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        return f"已触发：--fx {' '.join(args) if args else '(off)'}"
    except OSError as error:
        return f"启动失败：{error}"


def call_tool(name: str, arguments: dict) -> str:
    if name == "xassistant_off":
        return run([])
    if name == "xassistant_confetti":
        count = max(1, min(int(arguments.get("count", 12) or 12), 12))
        return run(["confetti"]) if count >= 12 else run(["confetti"])
    if name == "xassistant_banner":
        text = str(arguments.get("text", "")).strip()[:40]
        if not text:
            return "text 不能为空"
        tone = str(arguments.get("tone", "info")).lower()
        if tone not in TONES:
            tone = "info"
        # 参数段与打字时同一套语法：写了秒数就是「秒-次数」，只写次数就用默认时长
        seconds, blinks = arguments.get("seconds"), arguments.get("blinks")
        spec = ""
        if seconds is not None:
            spec = f"{float(seconds)}-{max(1, min(int(blinks or 1), 5))}"
        elif blinks is not None:
            spec = str(max(1, min(int(blinks), 5)))
        return run([tone, spec, text] if spec else [tone, text])
    return f"未知工具：{name}"


def reply(result=None, error=None, msg_id=None) -> None:
    body: dict = {"jsonrpc": "2.0", "id": msg_id}
    if error is not None:
        body["error"] = error
    else:
        body["result"] = result
    sys.stdout.write(json.dumps(body, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def handle(request: dict) -> None:
    method = request.get("method")
    msg_id = request.get("id")
    params = request.get("params") or {}
    if method == "initialize":
        reply({
            "protocolVersion": params.get("protocolVersion") or PROTOCOL_VERSION,
            "capabilities": {"tools": {}},
            "serverInfo": SERVER_INFO,
        }, msg_id=msg_id)
    elif method in ("notifications/initialized", "initialized"):
        pass                                            # 通知不需要回执
    elif method == "ping":
        reply({}, msg_id=msg_id)
    elif method == "tools/list":
        reply({"tools": TOOLS}, msg_id=msg_id)
    elif method == "tools/call":
        text = call_tool(params.get("name", ""), params.get("arguments") or {})
        reply({"content": [{"type": "text", "text": text}], "isError": text.startswith(("找不到", "启动失败", "未知"))},
              msg_id=msg_id)
    elif msg_id is not None:
        reply(error={"code": -32601, "message": f"unsupported method: {method}"}, msg_id=msg_id)


def main() -> int:
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            handle(json.loads(line))
        except json.JSONDecodeError:
            reply(error={"code": -32700, "message": "parse error"})
    return 0


if __name__ == "__main__":
    sys.exit(main())
