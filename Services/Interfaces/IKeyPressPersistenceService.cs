using XAssistant.Models;

namespace XAssistant.Services.Interfaces;

public interface IKeyPressPersistenceService
{
    bool TryEnqueue(KeyPressRecord record);
    bool Flush(TimeSpan timeout);
    bool FlushAndStop(TimeSpan timeout);
}
