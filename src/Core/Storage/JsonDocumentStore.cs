using System.Text.Json;

namespace PairwiseGsb.Core.Storage;

public sealed class JsonDocumentStore
{
    private readonly SemaphoreSlim syncRoot = new(1, 1);
    private readonly string path;
    private StoreDocument document;

    public JsonDocumentStore(string path)
    {
        this.path = path;
        document = Load();
    }

    public const int CurrentFormatVersion = 1;

    public async Task<T> Read<T>(Func<StoreDocument, T> action)
    {
        await syncRoot.WaitAsync();
        try
        {
            return action(document);
        }
        finally
        {
            syncRoot.Release();
        }
    }

    public async Task<T> Mutate<T>(Func<StoreDocument, T> action)
    {
        await syncRoot.WaitAsync();
        try
        {
            var transaction = Clone(document);
            var result = action(transaction);
            Save(transaction);
            document = transaction;
            return result;
        }
        finally
        {
            syncRoot.Release();
        }
    }

    public Task Mutate(Action<StoreDocument> action)
    {
        return Mutate(document =>
        {
            action(document);
            return true;
        });
    }

    private StoreDocument Load()
    {
        if (!File.Exists(path))
        {
            return new StoreDocument();
        }

        var json = File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize<StoreDocument>(json, CanonicalJson.Options) ?? new StoreDocument();
        return Migrate(loaded);
    }

    private static StoreDocument Clone(StoreDocument value)
    {
        return JsonSerializer.Deserialize<StoreDocument>(JsonSerializer.Serialize(value, CanonicalJson.Options), CanonicalJson.Options)
            ?? new StoreDocument();
    }

    private static StoreDocument Migrate(StoreDocument loaded)
    {
        if (loaded.FormatVersion > CurrentFormatVersion)
        {
            throw new InvalidOperationException($"数据格式版本 {loaded.FormatVersion} 高于本应用支持的 {CurrentFormatVersion}。");
        }

        loaded.FormatVersion = CurrentFormatVersion;
        return loaded;
    }

    private void Save(StoreDocument value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var json = JsonSerializer.Serialize(value, CanonicalJson.Options);
        var tempPath = $"{path}.{Environment.CurrentManagedThreadId}.tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, true);
    }
}
