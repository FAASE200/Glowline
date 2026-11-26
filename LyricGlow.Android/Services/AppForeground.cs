namespace LyricGlow.Android.Services;

public static class AppForeground
{
    public static bool Active { get; private set; } = true;
    public static event Action<bool>? Changed;

    public static void Set(bool active)
    {
        if (Active == active) return;
        Active = active;
        Changed?.Invoke(active);
    }
}
