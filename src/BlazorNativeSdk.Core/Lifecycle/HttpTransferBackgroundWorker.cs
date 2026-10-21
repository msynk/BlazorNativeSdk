namespace BlazorNativeSdk.Lifecycle;

/// <summary>
/// The worker that <see cref="HttpTransferOptions.UseBackgroundWork"/> schedules as <see cref="HttpTransferService.BackgroundWorkName"/>.
/// A run keeps the transfer queue moving until every transfer has finished; when transfers still wait for the network or a
/// later retry it asks the scheduler to run it again.
/// </summary>
internal sealed class HttpTransferBackgroundWorker(HttpTransferService transfers) : IBackgroundWorker
{
    public async ValueTask<WorkResult> ExecuteAsync(WorkContext context, CancellationToken cancellationToken) =>
        await transfers.RunPendingAsync(cancellationToken).ConfigureAwait(false)
            ? WorkResult.Retry("Transfers are waiting for the network or a retry delay.")
            : WorkResult.Success();
}
