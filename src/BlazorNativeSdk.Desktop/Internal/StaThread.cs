using System.Runtime.Versioning;

namespace BlazorNativeSdk.Desktop.Internal;

/// <summary>Runs shell (COM-based) Win32 calls on a dedicated single-threaded apartment thread.</summary>
/// <remarks>
/// Shell APIs such as <c>SHOpenFolderAndSelectItems</c> and <c>SHFileOperation</c> require COM to be initialized, and
/// thread-pool threads are MTA. A short-lived STA thread (the runtime calls <c>CoInitializeEx</c> for it) keeps these calls
/// independent of the caller's thread, including UI threads.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class StaThread
{
    public static Task<T> RunAsync<T>(Func<T> function)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(function());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "BlazorNativeSdk shell",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
