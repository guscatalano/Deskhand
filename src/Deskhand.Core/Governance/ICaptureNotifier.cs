namespace Deskhand.Core.Governance;

/// <summary>
/// Shows the user a visible notification when a screenshot is taken. Implemented by the host
/// (the HTTP host draws an on-screen toast); the governed backend calls it after every capture
/// unless <see cref="ControlState.NotifyOnCapture"/> is off.
/// </summary>
public interface ICaptureNotifier
{
    void Notify(string message);
}
