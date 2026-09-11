# 随机打字练习

随机练习位于热力图下，直接订阅现有键盘钩子的 TextInput 事件，无需点击输入框。它跟随键盘记录的开始和暂停；钩子仍正常传播按键，其他应用可正常接收输入。Ctrl、Alt、Win 组合键不写入练习。字符按前台键盘布局转换，支持 Shift 标点、空格、退格和回车。

正确字符高亮，回车检查时错误单词标红。完成后回车换句，也可开启自动换句。大小写不敏感，标点照写，弯直引号等价。每句完成只计分一次：max(10, 字符数 × 2 + 100 − 错误提交次数 × 10)。

成绩保存在 `%APPDATA%/XAssistant/practice-scores.json`（Debug 为 `XAssistant_Dev`）。历史成绩保留，损坏文件不会被覆盖。界面保留成绩与最近练习，不展示玩法说明。

## 题库

`Assets/Practice/sentences.json` 随构建和发布复制。修改后重启读取；添加唯一 ID 的句子即可扩展题库，分类自动生成。每句最多 300 字符、28 个词。分类与句数以当前 JSON 为准。

```json
{
  "version": 1,
  "sentences": [{
    "id": "example",
    "category": "数字练习",
    "source": "原创练习",
    "sourceUrl": "",
    "translation": "一。",
    "words": [{"text": "One", "ipa": "wʌn", "translation": "一", "suffix": "."}]
  }]
}
```

词之间自动插入一个空格，标点放在 suffix；每词必须有音标与释义。音标为静态文本，无语音 API。莎士比亚题目来源链接记录于 sourceUrl；译文和词义为练习用简译。

出题约束：

- 一个 `text` 只写一个词，不掺空格。切词逻辑 `PracticeText.Tokens`（正则 `[\p{L}\p{N}]+(?:'[\p{L}\p{N}]+)*`）把 `summer's`、`o'clock`、`Let's` 视为一个词，而 `well-being`、`'em` 会被切开或丢首撇号；音标与释义逐词对齐，词被拆碎后注释就对不上正文。
- `suffix` 只放标点（`. , ; : ! ?`），不能含字母数字；标点计入抄写字符数，抄写时忽略大小写、弯直引号等价，因此正文里的弯引号要写成 `PracticeText.Normalize` 后的直引号形式。
- 数字题把阿拉伯数字写进 `text`，音标写读法（如 `42` → `ˌfɔːti ˈtuː`），释义写中文数词；标点会把 `11:30` 拆成两段，避免这类写法。
- 一句可以含多个句子（句号后照常大写），例如 `Talk is cheap. Show me the code.`；`Text` 由 `text + suffix` 以单空格拼接，正文长度与词数分别受 300 字符与 28 词限制（输入框自带上限 320 字符）。
- 校验以真实代码为准：`PracticeCatalog.Validate` 拒绝空字段、重复 ID、超限句子；`release/typing-games/catalog-check` 用编译产物复现该加载路径，并逐句检查切词与单词一一对应。

## 验证

模拟钩子订阅与原生钩子回调验证字符输入、Shift 标点、快捷键过滤、退格、回车和事件解绑；WPF 深浅主题及完整单页离屏渲染检查。未启动真实全局监听，未改动真实统计数据。
