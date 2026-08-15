namespace Deskhand.Core.Governance;

/// <summary>
/// A persistent, always-on-top visual indicator the user cannot miss while a sensitive observation is
/// ongoing (e.g. the user's own mouse/keyboard being recorded). Unlike <see cref="ICaptureNotifier"/>
/// (a brief toast), this stays on screen from <see cref="Begin"/> until <see cref="End"/>. Implemented by
/// the host (the UI project draws a banner); no-op when no indicator is wired.
/// </summary>
public interface IActivityIndicator
{
    void Begin(string message);
    void End();
}
