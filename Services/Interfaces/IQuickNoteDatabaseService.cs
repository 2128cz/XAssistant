using System.Threading.Tasks;

namespace XAssistant.Services.Interfaces;

// 速记库写入服务：XAssistant 原生速记窗直插 xapp 主库 quick_notes 表（写入契约见 xapp ADR 0010）
public interface IQuickNoteDatabaseService
{
    // 保存一条速记，只写 content/source，时间戳靠 DB 默认；成功返回 true，失败返回 false（内容由调用方保留以便重试）
    Task<bool> SaveAsync(string content, string? source);
}
