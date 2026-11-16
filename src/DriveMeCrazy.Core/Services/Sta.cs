namespace DriveMeCrazy.Core.Services;

/// <summary>Runs work on a dedicated STA thread (the Windows Update Agent COM API is happiest there).</summary>
internal static class Sta
{
    public static Task<T> Run<T>(Func<T> work, string name = "DriveMeCrazy worker")
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
