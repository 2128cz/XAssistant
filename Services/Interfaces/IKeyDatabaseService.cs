using System.Collections.Generic;
using XAssistant.Models;

namespace XAssistant.Services.Interfaces;

public interface IKeyDatabaseService
{
    /// <summary>登记一条按键记录；实现方只入队，落库在后台线程完成。</summary>
    void SaveKeyPress(KeyPressRecord record);

    /// <summary>等已登记的记录全部写完，读库做全量统计前与退出前调用。</summary>
    void Flush();

    Dictionary<string, int> GetKeyCounts();
    Dictionary<string, int> GetKeyCounts(DateTime? from, DateTime? to);
}
