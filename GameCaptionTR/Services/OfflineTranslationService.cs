using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace GameCaptionTR.Services;

/// <summary>
/// Yerel ONNX çeviri (İngilizce → Türkçe). İnternet yalnızca ilk model indirmede gerekir.
/// </summary>
public sealed class OfflineTranslationService : IDisposable
{
    private const string ModelBaseUrl =
        "https://huggingface.co/onnx-community/opus-mt-tc-big-en-tr/resolve/main/";

    private static readonly string ModelDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameCaptionTR",
        "models",
        "tc-big-en-tr");

    private static readonly (string File, string Url)[] ModelFiles =
    [
        ("source.spm", ModelBaseUrl + "source.spm"),
        ("target.spm", ModelBaseUrl + "target.spm"),
        ("vocab.json", ModelBaseUrl + "vocab.json"),
        ("config.json", ModelBaseUrl + "config.json"),
        ("generation_config.json", ModelBaseUrl + "generation_config.json"),
        ("onnx/encoder_model_quantized.onnx", ModelBaseUrl + "onnx/encoder_model_quantized.onnx"),
        ("onnx/decoder_model_merged_quantized.onnx", ModelBaseUrl + "onnx/decoder_model_merged_quantized.onnx"),
    ];

    private readonly HttpClient _http;
    private readonly object _initLock = new();

    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private SentencePieceTokenizer? _sourceTokenizer;
    private Dictionary<string, int>? _vocab;
    private Dictionary<int, string>? _reverseVocab;
    private int _decoderStartTokenId;
    private int _eosTokenId;
    private int _padTokenId;
    private int _numLayers;
    private int _numHeads;
    private int _headDim;
    private bool _ready;

    public OfflineTranslationService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GameCaptionTR/1.0");
    }

    public bool IsModelInstalled => ModelFiles.All(f => File.Exists(Path.Combine(ModelDir, f.File)));

    public event Action<string>? StatusChanged;

    public async Task EnsureModelAsync(CancellationToken cancellationToken)
    {
        if (IsModelInstalled)
        {
            return;
        }

        Directory.CreateDirectory(ModelDir);
        var total = ModelFiles.Length;
        for (var i = 0; i < total; i++)
        {
            var (file, url) = ModelFiles[i];
            var dest = Path.Combine(ModelDir, file);
            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            StatusChanged?.Invoke($"Çevrimdışı model indiriliyor ({i + 1}/{total})…");
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = File.Create(dest);
            await stream.CopyToAsync(fileStream, cancellationToken);
        }

        _ready = false;
        StatusChanged?.Invoke("Çevrimdışı model indirildi.");
    }

    public async Task<string> TranslateAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        await EnsureModelAsync(cancellationToken);
        EnsureSessions();

        var inputIds = EncodeSource(text);
        if (inputIds.Length == 0)
        {
            return string.Empty;
        }

        if (inputIds.Length > 256)
        {
            inputIds = inputIds[..256];
        }

        var attentionMask = Enumerable.Repeat(1L, inputIds.Length).ToArray();
        var encoderInputIds = new DenseTensor<long>(inputIds, [1, inputIds.Length]);
        var encoderAttention = new DenseTensor<long>(attentionMask, [1, inputIds.Length]);

        using var encoderOutputs = _encoder!.Run([
            NamedOnnxValue.CreateFromTensor("input_ids", encoderInputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", encoderAttention)
        ]);

        var encoderHidden = encoderOutputs.First().AsTensor<float>();
        var encoderHiddenShape = encoderHidden.Dimensions.ToArray();

        var generated = GenerateTokens(encoderHidden, encoderHiddenShape, attentionMask, cancellationToken);
        return DecodeTarget(generated);
    }

    private long[] EncodeSource(string text)
    {
        string? normalizedString = null;
        var tokens = _sourceTokenizer!.EncodeToTokens(
            text,
            out normalizedString,
            addBeginningOfSentence: false,
            addEndOfSentence: false,
            considerPreTokenization: true,
            considerNormalization: true);

        var ids = new List<long>(tokens.Count + 1);
        foreach (var token in tokens)
        {
            if (_vocab!.TryGetValue(token.Value, out var id))
            {
                ids.Add(id);
            }
        }

        ids.Add(_eosTokenId);
        return ids.ToArray();
    }

    private string DecodeTarget(IReadOnlyList<long> tokenIds)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var id in tokenIds)
        {
            if (id == _padTokenId || id == _eosTokenId)
            {
                continue;
            }

            if (_reverseVocab!.TryGetValue((int)id, out var piece))
            {
                builder.Append(piece);
            }
        }

        return builder
            .ToString()
            .Replace('\u2581', ' ')
            .Trim();
    }

    private List<long> GenerateTokens(
        Tensor<float> encoderHidden,
        int[] encoderHiddenShape,
        long[] attentionMask,
        CancellationToken cancellationToken)
    {
        var generated = new List<long> { _decoderStartTokenId };
        var pastKeyValues = CreateEmptyPastKeyValues();
        var useCacheBranch = false;

        const int maxSteps = 128;
        for (var step = 0; step < maxSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var decoderInput = new[] { generated[^1] };
            var decTensor = new DenseTensor<long>(decoderInput, [1, 1]);
            var encMaskTensor = new DenseTensor<long>(attentionMask, [1, attentionMask.Length]);
            var encHiddenTensor = new DenseTensor<float>(encoderHidden.ToArray(), encoderHiddenShape);
            var cacheBranchTensor = new DenseTensor<bool>(new[] { useCacheBranch }, [1]);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", decTensor),
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", encMaskTensor),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encHiddenTensor),
                NamedOnnxValue.CreateFromTensor("use_cache_branch", cacheBranchTensor)
            };

            foreach (var pair in pastKeyValues)
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(pair.Key, pair.Value));
            }

            using var decoderOutputs = _decoder!.Run(inputs);
            var outputMap = decoderOutputs.ToDictionary(
                output => output.Name,
                output => output.AsTensor<float>());

            var logits = outputMap["logits"];
            var vocabSize = logits.Dimensions[^1];
            var lastIndex = logits.Dimensions[1] - 1;
            var nextId = ArgMax(logits, lastIndex, vocabSize);

            if (nextId == _eosTokenId)
            {
                break;
            }

            generated.Add(nextId);
            UpdatePastKeyValues(pastKeyValues, outputMap, step == 0);
            useCacheBranch = true;
        }

        return generated.Skip(1).ToList();
    }

    private Dictionary<string, DenseTensor<float>> CreateEmptyPastKeyValues()
    {
        var caches = new Dictionary<string, DenseTensor<float>>(StringComparer.Ordinal);
        for (var layer = 0; layer < _numLayers; layer++)
        {
            caches[$"past_key_values.{layer}.decoder.key"] = EmptyCacheTensor();
            caches[$"past_key_values.{layer}.decoder.value"] = EmptyCacheTensor();
            caches[$"past_key_values.{layer}.encoder.key"] = EmptyCacheTensor();
            caches[$"past_key_values.{layer}.encoder.value"] = EmptyCacheTensor();
        }

        return caches;
    }

    private DenseTensor<float> EmptyCacheTensor()
    {
        return new DenseTensor<float>(Array.Empty<float>(), [1, _numHeads, 0, _headDim]);
    }

    private void UpdatePastKeyValues(
        Dictionary<string, DenseTensor<float>> pastKeyValues,
        Dictionary<string, Tensor<float>> outputs,
        bool storeEncoderCache)
    {
        for (var layer = 0; layer < _numLayers; layer++)
        {
            pastKeyValues[$"past_key_values.{layer}.decoder.key"] =
                ToDenseTensor(outputs[$"present.{layer}.decoder.key"]);
            pastKeyValues[$"past_key_values.{layer}.decoder.value"] =
                ToDenseTensor(outputs[$"present.{layer}.decoder.value"]);

            if (storeEncoderCache)
            {
                pastKeyValues[$"past_key_values.{layer}.encoder.key"] =
                    ToDenseTensor(outputs[$"present.{layer}.encoder.key"]);
                pastKeyValues[$"past_key_values.{layer}.encoder.value"] =
                    ToDenseTensor(outputs[$"present.{layer}.encoder.value"]);
            }
        }
    }

    private static DenseTensor<float> ToDenseTensor(Tensor<float> tensor)
    {
        return new DenseTensor<float>(tensor.ToArray(), tensor.Dimensions.ToArray());
    }

    private void EnsureSessions()
    {
        if (_ready)
        {
            return;
        }

        lock (_initLock)
        {
            if (_ready)
            {
                return;
            }

            LoadVocab(Path.Combine(ModelDir, "vocab.json"));
            LoadModelConfig(Path.Combine(ModelDir, "config.json"));
            LoadTokenIdsFromConfig(Path.Combine(ModelDir, "generation_config.json"));

            using (var sourceStream = File.OpenRead(Path.Combine(ModelDir, "source.spm")))
            {
                _sourceTokenizer = SentencePieceTokenizer.Create(
                    sourceStream,
                    addBeginOfSentence: false,
                    addEndOfSentence: false);
            }

            var encoderPath = Path.Combine(ModelDir, "onnx", "encoder_model_quantized.onnx");
            var decoderPath = Path.Combine(ModelDir, "onnx", "decoder_model_merged_quantized.onnx");

            _encoder = new InferenceSession(encoderPath);
            _decoder = new InferenceSession(decoderPath);
            _ready = true;
        }
    }

    private void LoadVocab(string vocabPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(vocabPath));
        _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        _reverseVocab = new Dictionary<int, string>();

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var id = property.Value.GetInt32();
            _vocab[property.Name] = id;
            _reverseVocab[id] = property.Name;
        }
    }

    private void LoadModelConfig(string configPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = doc.RootElement;
        _numLayers = root.GetProperty("decoder_layers").GetInt32();
        _numHeads = root.GetProperty("decoder_attention_heads").GetInt32();
        var hiddenSize = root.GetProperty("d_model").GetInt32();
        _headDim = hiddenSize / _numHeads;
    }

    private void LoadTokenIdsFromConfig(string configPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;

            if (root.TryGetProperty("decoder_start_token_id", out var start))
            {
                _decoderStartTokenId = start.GetInt32();
            }

            if (root.TryGetProperty("eos_token_id", out var eos))
            {
                _eosTokenId = eos.ValueKind == JsonValueKind.Array
                    ? eos[0].GetInt32()
                    : eos.GetInt32();
            }

            if (root.TryGetProperty("pad_token_id", out var pad))
            {
                _padTokenId = pad.GetInt32();
            }
        }
        catch
        {
            _decoderStartTokenId = 57059;
            _padTokenId = 57059;
            _eosTokenId = 43741;
        }
    }

    private static long ArgMax(Tensor<float> logits, int sequenceIndex, int vocabSize)
    {
        long bestId = 0;
        var best = float.MinValue;
        for (var v = 0; v < vocabSize; v++)
        {
            var score = logits[0, sequenceIndex, v];
            if (score > best)
            {
                best = score;
                bestId = v;
            }
        }

        return bestId;
    }

    public void Dispose()
    {
        _encoder?.Dispose();
        _decoder?.Dispose();
        _http.Dispose();
    }
}
