using System.Collections.Generic;
using XAssistant.Models;

namespace XAssistant.Services.Interfaces;

public interface IKeyDatabaseService
{
    void SaveKeyPress(KeyPressRecord record);
    void SaveKeyPressBatch(IReadOnlyCollection<KeyPressRecord> records);
    Dictionary<string, int> GetKeyCounts();
    Dictionary<string, int> GetKeyCounts(DateTime? from, DateTime? to);
}
