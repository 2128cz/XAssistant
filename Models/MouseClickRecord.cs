using System;

namespace XAssistant.Models;

public class MouseClickRecord
{
    public long Id { get; set; }            // 自增主键
    public required string Button { get; set; }      // "Left", "Middle", "Right"
    public DateTime ClickTime { get; set; } // 精确时间
}