using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Photo;

namespace AIArchitectStudio.ViewModels;

/// <summary>
/// Волна 4.1 — облачный маршрут «Реконструкции по фото и плану»: живой двухходовой диалог с моделью облачного сервиса (снимок +
/// оптический паспорт → JSON v2 → проверка ядром → замечания → исправленный JSON), лента диалога в чате (ответы модели, сверка
/// чтения снимка с Визуальным ядром, замечания ядра), вставка итоговой проверенной модели одним шагом Undo.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Последний живой диалог по фото (для отчёта и отладки).</summary>
    public PhotoDialogueResult? LastPhotoDialogue { get; private set; }

    public async Task<PhotoDialogueResult?> RunCloudPhotoDialogueAsync(string model, PhotoArchitectureDescriptor descriptor, PhotoLiftOptions options, byte[] jpeg, string name,
        PhotoLiftResult? visualCore, bool insert = true, string? helper = null)
    {
        if (UnifiedCloudAiClient.Shared is not { } client)
        {
            ChatMessages.Add(ChatMessageModel.System(Loc.Get("W41NoCloud")));
            if (insert) InsertPhotoResult(visualCore ?? await Task.Run(() => LiftPhoto(descriptor, options)), jpeg, name);
            return null;
        }
        string title = UnifiedCloudAiClient.SpecOf(model)?.Title ?? model;
        var user = ChatMessageModel.User($"[{Loc.Get("W4StudioTitle")}] {name}");
        user.Thumbnails = [jpeg];
        ChatMessages.Add(user);
        ChatMessages.Add(ChatMessageModel.System(Loc.Format("W41DialogStart", name, title)));
        PinPhotoReference(jpeg, name);
        string cloud = Loc.Get("W41Cloud"), core = Loc.Get("W41Engine");
        var progress = new Progress<PhotoDialogueEvent>(e =>
        {
            switch (e.Kind)
            {
                case "answer" when e.Call is { } c:
                    ChatMessages.Add(ChatMessageModel.Assistant(e.Text + "\n" + ModelGist(c.Text), cloud + " · " + (UnifiedCloudAiClient.SpecOf(c.ModelUsed)?.Title ?? c.ModelUsed)));
                    break;
                case "review":
                case "reading":
                    ChatMessages.Add(ChatMessageModel.Assistant(e.Text, core));
                    break;
                case "reconcile":
                    break;
                case "sent" when e.Turn > 1:
                    ChatMessages.Add(ChatMessageModel.System(Loc.Format("W41Turn", e.Turn) + ": " + e.Text.Split('\n')[0]));
                    break;
                case "error":
                case "skip":
                    ChatMessages.Add(ChatMessageModel.System(e.Text));
                    break;
            }
            // Строка состояния: этап и процент (поток ответа — счётчик символов, ядро — этап проверки и доводки).
            string stage = e.Kind switch
            {
                "stream" or "progress" => e.Text,
                "sent" => Loc.Format("W41Turn", e.Turn) + " · " + title + " · " + Loc.Get("W41Sending"),
                "answer" => Loc.Format("W41Turn", e.Turn) + " · " + Loc.Get("W41Checking"),
                "review" or "skip" => Loc.Get("W41Engine") + " · " + Loc.Get("W41Checking"),
                "final" => Loc.Get("W41Done"),
                _ => BusyText
            };
            if (e.Percent >= 0) BusyPercent = Math.Max(BusyPercent, e.Percent);
            BusyText = BusyPercent >= 0 ? $"{stage} · {BusyPercent:0} %" : stage;
        });
        IsBusy = true;
        BusyPercent = 3;
        BusyText = Loc.Format("BusyCloud", title) + " · 3 %";
        PhotoDialogueResult? result = null;
        try
        {
            result = await UniversalVisionModelBridge.RunDialogueAsync(client, _ai, model, descriptor, jpeg, options, PhotoLoads, progress, helper: helper);
            LastPhotoDialogue = result;
            // Сверка чтения снимка моделью — одной строкой.
            if (result.Target is { ReconcileNotes.Count: > 0 } t) ChatMessages.Add(ChatMessageModel.Assistant("Сверка с Визуальным ядром: " + string.Join("; ", t.ReconcileNotes), core));
            ChatMessages.Add(ChatMessageModel.System(Loc.Format("W41Tokens", title, result.InputTokens.ToString("N0", Loc.Culture), result.OutputTokens.ToString("N0", Loc.Culture),
                result.ModelSeconds.ToString("0.0", Loc.Culture))));
            if (insert && result.Lift is { } lift) InsertPhotoResult(lift, jpeg, name);
        }
        catch (Exception ex) when (ex is AiProviderException or HttpRequestException or InvalidOperationException)
        {
            ShowError(Loc.Get("ErrorAi"), ex.Message, ex is AiProviderException ape ? ape.Code : "AIA-PHOTO-CLOUD", ex.ToString());
            if (insert) InsertPhotoResult(visualCore ?? await Task.Run(() => LiftPhoto(descriptor, options)), jpeg, name);
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            BusyPercent = -1;
        }
        return result;
    }

    /// <summary>Суть ответа модели для ленты (без JSON): message и photoReading.</summary>
    private static string ModelGist(string text)
    {
        if (!AiJsonExtractor.TryExtract(text, out var doc)) return "ответ без JSON";
        var reading = PhotoReading.Parse(doc.RawJson);
        string msg = string.IsNullOrWhiteSpace(doc.Message) ? string.Empty : "«" + AiHttp.Truncate(doc.Message.Trim(), 220) + "» ";
        return msg + string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"· элементов JSON {doc.Elements.Count}") + (reading is not null ? " · photoReading: " + reading.Describe() : string.Empty);
    }
}
