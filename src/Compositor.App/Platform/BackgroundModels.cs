using System.Net.Http;
using System.Text.Json;
using Compositor.Editing;
using Compositor.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Compositor.App.Platform;

/// <summary>A downloadable subject-segmentation model for Remove Background (all BiRefNet, MIT License).</summary>
public sealed record BackgroundModel(string Id, string Name, string Repo, string File, long Bytes, string Summary, bool Recommended)
{
    public string SizeText => Bytes >= 1_000_000_000 ? $"{Bytes / 1e9:0.0} GB" : $"{Bytes / 1e6:0} MB";
    public string Page => $"https://huggingface.co/{Repo}";
}

/// <summary>What is installed, kept beside the models (LocalAppData\LayerForm\Models\models.json).</summary>
public sealed class InstalledModels
{
    public string? Active { get; set; }
    /// <summary>Run on the CPU instead of the graphics card: slower to start, but uses much less memory.</summary>
    public bool UseCpu { get; set; }
    public Dictionary<string, InstalledModel> Models { get; set; } = new();

    public sealed class InstalledModel
    {
        /// <summary>The Hugging Face revision the file came from; an update is a newer revision.</summary>
        public string Revision { get; set; } = "";
        public DateTime Downloaded { get; set; }
        public long Bytes { get; set; }
    }
}

/// <summary>Remove Background's models: download on first use (asking which), switch, check for updates, remove.</summary>
public static class BackgroundModels
{
    public static readonly BackgroundModel[] Catalog =
    {
        new("birefnet-lite", "BiRefNet Lite", "onnx-community/BiRefNet_lite-ONNX", "onnx/model_fp16.onnx", 114_538_221,
            "Fast, clean cut-outs for most photos — people, products, pets. Recommended.", true),
        new("birefnet", "BiRefNet (best quality)", "onnx-community/BiRefNet-ONNX", "onnx/model_fp16.onnx", 489_666_272,
            "The finest edges for hair, fur and detailed outlines. Larger and slower.", false),
    };

    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LayerForm", "Models");
    private static readonly string StatePath = Path.Combine(Root, "models.json");
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(60) };
    private static InstalledModels? state;
    private static BiRefNetSegmenter? loaded;

    static BackgroundModels() => http.DefaultRequestHeaders.UserAgent.ParseAdd("LayerForm/1.0 (+Windows)");

    public static InstalledModels State
    {
        get
        {
            if (state != null) return state;
            if (System.IO.File.Exists(StatePath))
                try { state = JsonSerializer.Deserialize<InstalledModels>(System.IO.File.ReadAllText(StatePath)); } catch { }
            state ??= new InstalledModels();
            // Forget entries whose file has gone.
            foreach (var id in state.Models.Keys.ToList()) if (!System.IO.File.Exists(ModelPath(id))) state.Models.Remove(id);
            if (state.Active != null && !state.Models.ContainsKey(state.Active)) state.Active = state.Models.Keys.FirstOrDefault();
            return state;
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Root);
            System.IO.File.WriteAllText(StatePath, JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Diagnostics.Log("Models state: " + e.Message); }
    }

    public static string ModelPath(string id) => Path.Combine(Root, id, "model.onnx");
    public static bool IsInstalled(string id) => State.Models.ContainsKey(id);
    public static bool AnyInstalled => State.Models.Count > 0;
    public static BackgroundModel? Active => Catalog.FirstOrDefault(m => m.Id == State.Active);

    /// <summary>Makes an installed model the one Remove Background uses (loaded lazily on the first cut-out).</summary>
    public static void Activate(string id)
    {
        if (!IsInstalled(id)) return;
        State.Active = id;
        Save();
        loaded?.Dispose();
        loaded = new BiRefNetSegmenter(ModelPath(id));
        SubjectRemoval.Segmenter = loaded;
        SubjectRemoval.ClearCache();
    }

    /// <summary>At launch: use the chosen model if it is installed.</summary>
    public static void Restore()
    {
        if (State.Active is { } id && IsInstalled(id)) Activate(id);
    }

    public static async Task<string> LatestRevision(BackgroundModel model, CancellationToken token = default)
    {
        using var response = await http.GetAsync($"https://huggingface.co/api/models/{model.Repo}", token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return json.RootElement.GetProperty("sha").GetString() ?? throw new InvalidOperationException("The model page gave no revision.");
    }

    /// <summary>Downloads (or updates) a model to a temporary file, then swaps it in; a cancelled download leaves nothing behind.</summary>
    public static async Task Download(BackgroundModel model, IProgress<(long Done, long Total)> progress, CancellationToken token)
    {
        string revision = await LatestRevision(model, token);
        string folder = Path.Combine(Root, model.Id);
        Directory.CreateDirectory(folder);
        string temp = Path.Combine(folder, "model.download");
        using (var response = await http.GetAsync($"https://huggingface.co/{model.Repo}/resolve/{revision}/{model.File}", HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? model.Bytes;
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            var buffer = new byte[1 << 20];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                progress.Report((done, total));
            }
            if (done != total) throw new IOException("The download ended early. Check your connection and try again.");
        }
        // Release the old file if this model is loaded, then replace it.
        if (State.Active == model.Id) { loaded?.Dispose(); loaded = null; SubjectRemoval.Segmenter = null; }
        System.IO.File.Move(temp, ModelPath(model.Id), overwrite: true);
        State.Models[model.Id] = new InstalledModels.InstalledModel { Revision = revision, Downloaded = DateTime.Now, Bytes = new FileInfo(ModelPath(model.Id)).Length };
        Save();
        if (State.Active == null || State.Active == model.Id) Activate(model.Id);
    }

    public static void Remove(string id)
    {
        if (State.Active == id) { loaded?.Dispose(); loaded = null; SubjectRemoval.Segmenter = null; SubjectRemoval.ClearCache(); State.Active = null; }
        State.Models.Remove(id);
        try { Directory.Delete(Path.Combine(Root, id), recursive: true); } catch (Exception e) { Diagnostics.Log("Remove model: " + e.Message); }
        if (State.Active == null && State.Models.Keys.FirstOrDefault() is { } other) Activate(other);
        Save();
    }

    /// <summary>Switches between the graphics card and the CPU; the model reloads on its next use.</summary>
    public static void SetUseCpu(bool cpu)
    {
        State.UseCpu = cpu;
        Save();
        if (State.Active is { } id) Activate(id);
    }

    public static void CleanPartialDownloads()
    {
        foreach (var model in Catalog)
            try { System.IO.File.Delete(Path.Combine(Root, model.Id, "model.download")); } catch { }
    }
}

/// <summary>BiRefNet through ONNX Runtime. It runs on the GPU with the most dedicated memory (DirectML); if that GPU can't
/// hold the model it tries the next one, and the CPU last. The model unloads after a while unused, giving memory back.</summary>
public sealed class BiRefNetSegmenter : ISubjectSegmenter, IDisposable
{
    private const int Size = 1024;
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f }, Std = { 0.229f, 0.224f, 0.225f };
    private readonly string path;
    private readonly object gate = new();
    private readonly System.Threading.Timer idle;
    private InferenceSession? session;
    /// <summary>Where to run, best first: DirectML adapter indices, then -1 for the CPU.</summary>
    private List<(int Device, string Name)>? candidates;
    private int attempt;
    public string Device { get; private set; } = "";

    public BiRefNetSegmenter(string path)
    {
        this.path = path;
        idle = new System.Threading.Timer(_ => { lock (gate) { session?.Dispose(); session = null; } GC.Collect(); });
    }

    private InferenceSession Session()
    {
        if (session != null) return session;
        candidates ??= BackgroundModels.State.UseCpu
            ? new List<(int, string)> { (-1, "CPU") }
            : GpuAdapters.Preferred().Select(a => (a.Index, a.Name)).Append((-1, "CPU")).ToList();
        while (attempt < candidates.Count)
        {
            var (device, name) = candidates[attempt];
            try
            {
                if (device < 0)
                    session = new InferenceSession(path, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, EnableCpuMemArena = false });
                else
                {
                    // DirectML wants sequential execution without memory-pattern planning.
                    var options = new SessionOptions
                    {
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, EnableMemoryPattern = false,
                        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, EnableCpuMemArena = false,
                    };
                    options.AppendExecutionProvider_DML(device);
                    session = new InferenceSession(path, options);
                }
                Device = name;
                Diagnostics.Log($"Remove Background runs on {name}");
                return session;
            }
            catch (Exception e)
            {
                Diagnostics.Log($"Couldn't load the model on {name}: {e.Message}");
                attempt++;
            }
        }
        throw new InvalidOperationException("The background removal model couldn't be loaded on this PC.");
    }

    public MaskImage SubjectMask(RasterImage image)
    {
        lock (gate)
        {
            // Resize to the model's square input; transparent pixels read as black, as in a flattened photo.
            using var square = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(square))
            {
                canvas.Clear(SKColors.Black);
                canvas.DrawImage(image.Image, new SKRect(0, 0, Size, Size), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }
            var pixels = square.GetPixelSpan();
            var input = new float[3 * Size * Size];
            for (int i = 0; i < Size * Size; i++)
                for (int c = 0; c < 3; c++)
                    input[c * Size * Size + i] = (pixels[i * 4 + c] / 255f - Mean[c]) / Std[c];

            float[] logits;
            while (true)
            {
                var s = Session();
                try { logits = Run(s, input); break; }
                catch (OnnxRuntimeException e) when (attempt < candidates!.Count - 1)
                {
                    // Usually out of memory on a small GPU: move on to the next device.
                    Diagnostics.Log($"Remove Background failed on {Device}, trying the next device: {e.Message}");
                    session?.Dispose();
                    session = null;
                    attempt++;
                }
            }

            // Sigmoid to coverage, then back to the image's own size.
            using var mask = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Gray8, SKAlphaType.Opaque));
            var span = mask.GetPixelSpan();
            for (int i = 0; i < Size * Size; i++) span[i] = (byte)Math.Round(255 / (1 + Math.Exp(-logits[i])));
            var scaled = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Gray8, SKAlphaType.Opaque));
            using (var small = SKImage.FromBitmap(mask))
            using (var canvas = new SKCanvas(scaled))
                canvas.DrawImage(small, new SKRect(0, 0, image.Width, image.Height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            idle.Change(TimeSpan.FromSeconds(45), Timeout.InfiniteTimeSpan);
            return MaskImage.Adopt(scaled);
        }
    }

    private static float[] Run(InferenceSession s, float[] input)
    {
        var inputName = s.InputMetadata.Keys.First();
        bool half = s.InputMetadata[inputName].ElementDataType == TensorElementType.Float16;
        var dims = new[] { 1, 3, Size, Size };
        NamedOnnxValue value = half
            ? NamedOnnxValue.CreateFromTensor(inputName, new DenseTensor<Float16>(input.Select(v => (Float16)v).ToArray(), dims))
            : NamedOnnxValue.CreateFromTensor(inputName, new DenseTensor<float>(input, dims));
        using var results = s.Run(new[] { value });
        return results.Last().Value switch
        {
            DenseTensor<float> f => f.Buffer.ToArray(),
            DenseTensor<Float16> h => h.Buffer.ToArray().Select(v => (float)v).ToArray(),
            Tensor<float> f => f.ToArray(),
            Tensor<Float16> h => h.ToArray().Select(v => (float)v).ToArray(),
            _ => throw new InvalidOperationException("The model returned an unexpected output."),
        };
    }

    public void Dispose()
    {
        idle.Dispose();
        lock (gate) { session?.Dispose(); session = null; }
    }
}
