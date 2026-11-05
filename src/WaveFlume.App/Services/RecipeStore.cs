using System.IO;
using System.Text.Json;
using WaveFlume.Core;

namespace WaveFlume.App.Services;

public interface IRecipeRepository
{
    Task<IReadOnlyList<WaveRecipe>> ListAsync();
    Task InsertAsync(WaveRecipe recipe);
    Task UpdateAsync(WaveRecipe recipe);
    Task DeleteAsync(string testNumber);
}

/// <summary>Recipe CRUD on a local JSON file (recipes.json in the data directory).</summary>
public sealed class RecipeStore(string path) : IRecipeRepository
{
    private readonly object _lock = new();

    public IReadOnlyList<WaveRecipe> List()
    {
        lock (_lock) return Read().OrderBy(r => r.TestNumber, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public WaveRecipe? Get(string testNumber) =>
        List().FirstOrDefault(r => string.Equals(r.TestNumber, testNumber, StringComparison.OrdinalIgnoreCase));

    /// <summary>Insert; fails when the test number already exists (wave_recipes.test_number is UNIQUE).</summary>
    public void Insert(WaveRecipe recipe) => Mutate(list =>
    {
        if (Find(list, recipe.TestNumber) >= 0) throw new InvalidOperationException($"Test number '{recipe.TestNumber}' already exists. Use Update.");
        list.Add(recipe);
    });

    public void Update(WaveRecipe recipe) => Mutate(list =>
    {
        int i = Find(list, recipe.TestNumber);
        if (i < 0) throw new InvalidOperationException($"Test number '{recipe.TestNumber}' not found. Use Save.");
        list[i] = recipe with { CreatedUtc = list[i].CreatedUtc };
    });

    public void Delete(string testNumber) => Mutate(list =>
    {
        int i = Find(list, testNumber);
        if (i >= 0) list.RemoveAt(i);
    });

    /// <summary>Next free "T-0001" style number.</summary>
    public string NextTestNumber()
    {
        var used = List().Select(r => r.TestNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++)
            if (!used.Contains($"T-{n:0000}")) return $"T-{n:0000}";
    }

    Task<IReadOnlyList<WaveRecipe>> IRecipeRepository.ListAsync() => Task.FromResult(List());
    Task IRecipeRepository.InsertAsync(WaveRecipe r) { Insert(r); return Task.CompletedTask; }
    Task IRecipeRepository.UpdateAsync(WaveRecipe r) { Update(r); return Task.CompletedTask; }
    Task IRecipeRepository.DeleteAsync(string testNumber) { Delete(testNumber); return Task.CompletedTask; }

    private static int Find(List<WaveRecipe> list, string testNumber) =>
        list.FindIndex(r => string.Equals(r.TestNumber, testNumber, StringComparison.OrdinalIgnoreCase));

    private void Mutate(Action<List<WaveRecipe>> change)
    {
        lock (_lock)
        {
            var list = Read();
            change(list);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, FlumeConfig.JsonOptions));
            File.Move(tmp, path, true);
        }
    }

    private List<WaveRecipe> Read() =>
        File.Exists(path) ? JsonSerializer.Deserialize<List<WaveRecipe>>(File.ReadAllText(path), FlumeConfig.JsonOptions) ?? [] : [];
}
