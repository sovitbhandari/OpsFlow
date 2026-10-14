using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using OpsFlow.Application;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Options;

namespace OpsFlow.Infrastructure.Storage;

public sealed class BlobAttachmentStorage(IOptions<AttachmentOptions> options) : IAttachmentStorage
{
    private readonly BlobContainerClient _container = new(new Uri(options.Value.BlobContainerUri), new DefaultAzureCredential());

    public async Task<string> SaveAsync(Guid organizationId, Guid workOrderId, Stream content, CancellationToken cancellationToken)
    {
        var key = $"{organizationId:N}/{workOrderId:N}/{Guid.NewGuid():N}";
        if (!AttachmentRules.IsSafeStorageKey(key))
        {
            throw new InvalidOperationException("Storage key is not safe.");
        }

        await _container.GetBlobClient(key).UploadAsync(content, overwrite: false, cancellationToken);
        return key;
    }

    public async Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        if (!AttachmentRules.IsSafeStorageKey(storageKey))
        {
            throw new InvalidOperationException("Storage key is not safe.");
        }

        var response = await _container.GetBlobClient(storageKey).DownloadStreamingAsync(cancellationToken: cancellationToken);
        return response.Value.Content;
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        if (!AttachmentRules.IsSafeStorageKey(storageKey))
        {
            throw new InvalidOperationException("Storage key is not safe.");
        }

        await _container.GetBlobClient(storageKey).DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }
}
