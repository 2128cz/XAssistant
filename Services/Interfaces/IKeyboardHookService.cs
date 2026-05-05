using System;

namespace XAssistant.Services.Interfaces;

public interface IKeyboardHookService
{
    event Action<string>? KeyPressed;
    void Start();
    void Stop();
}
