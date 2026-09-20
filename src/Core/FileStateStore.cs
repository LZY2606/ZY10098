using System.Text.Json;

namespace NetCompare.Core;

public interface IStateStore
{
    Task<T> Read<T>(Func<PersistedState, T> reader, CancellationToken cancellationToken = default);
    Task Mutate(Func<PersistedState, Task> mutation, CancellationToken cancellationToken = default);
    Task<T> Mutate<T>(Func<PersistedState, Task<T>> mutation, CancellationToken cancellationToken = default);
}

public sealed class FileStateStore : IStateStore, IAsyncDisposable
{
    private readonly string path;
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private PersistedState state = new();

    public FileStateStore(string? directory = null)
    {
        directory ??= Path.Combine(Directory.GetCurrentDirectory(), ".netcompare");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "state.json");
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            state = JsonSerializer.Deserialize<PersistedState>(json, Hashing.JsonOptions) ?? new PersistedState();
        }
    }

    public async Task<T> Read<T>(Func<PersistedState, T> reader, CancellationToken cancellationToken = default)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            return reader(state);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task Mutate(Func<PersistedState, Task> mutation, CancellationToken cancellationToken = default)
    {
        await Mutate(async state =>
        {
            await mutation(state);
            return true;
        }, cancellationToken);
    }

    public async Task<T> Mutate<T>(Func<PersistedState, Task<T>> mutation, CancellationToken cancellationToken = default)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var working = Clone(state);
            var result = await mutation(working);
            var json = JsonSerializer.Serialize(working, new JsonSerializerOptions(Hashing.JsonOptions)
            {
                WriteIndented = true
            });
            var temp = path + ".tmp-" + Environment.ProcessId;
            await File.WriteAllTextAsync(temp, json, cancellationToken);
            File.Move(temp, path, overwrite: true);
            state = working;
            return result;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static PersistedState Clone(PersistedState value) =>
        JsonSerializer.Deserialize<PersistedState>(
            JsonSerializer.Serialize(value, Hashing.JsonOptions), Hashing.JsonOptions)
        ?? new PersistedState();

    public ValueTask DisposeAsync()
    {
        semaphore.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class NotFoundException(string message) : Exception(message);
public sealed class ConflictException(string message) : Exception(message);
