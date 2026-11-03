namespace Jarvis.Contracts.Speech;

/// <summary>An engine that can free its model's memory; it loads it again on next use.</summary>
public interface IUnloadable
{
    void Unload();
}
