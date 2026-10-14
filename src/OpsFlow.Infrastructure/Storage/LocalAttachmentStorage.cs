using Microsoft.Extensions.Options;
using OpsFlow.Application;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Options;

namespace OpsFlow.Infrastructure.Storage;

public sealed class LocalAttachmentStorage(IOptions<AttachmentOptions> options) : IAttachmentStorage
{
    public async Task<string> SaveAsync(Guid organizationId, Guid workOrderId, Stream content, CancellationToken cancellationToken)
    {
        var key = $"{organizationId:N}/{workOrderId:N}/{Guid.NewGuid():N}";
        var path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(file, cancellationToken);
        return key;
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        var path = ResolvePath(storageKey);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        var path = ResolvePath(storageKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string ResolvePath(string storageKey)
    {
        if (!AttachmentRules.IsSafeStorageKey(storageKey))
        {
            throw new InvalidOperationException("Storage key is not safe.");
        }

        var root = Path.GetFullPath(options.Value.LocalPath);
        var path = Path.GetFullPath(Path.Combine(root, storageKey));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Storage key escaped the attachment root.");
        }

        return path;
    }
}
