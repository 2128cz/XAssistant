using System;

namespace XAssistant.Services.Interfaces;

public interface IKeyboardHookService
{
    event Action<string>? KeyPressed;
    event Action<string>? TextInput;
    void Start();
    void Stop();
}
