using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Photo;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIArchitectStudio.ViewModels;

/// <summary>
/// Волна 4 — «Реконструкция по фото и плану»: открытие студии (кнопка верхнего бара, перетаскивание, Ctrl+Shift+V), подъём
/// выбранной моделью (Визуальное ядро, основная локальная модель, Совет КБ, веб-проектировщик) с гарантией совпадения формы и
/// цветов со снимком и проверкой каркаса, вставка в проект одним шагом Undo и закреплённая карточка «Фото-референс».
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Открыть студию; аргумент — уже загруженный снимок (перетаскивание, буфер обмена) или null.</summary>
    public event EventHandler<LoadedPhoto?>? PhotoStudioRequested;

    [RelayCommand]
    private void OpenPhotoStudio() => PhotoStudioRequested?.Invoke(this, null);

    public void RequestPhotoStudio(LoadedPhoto? photo) => PhotoStudioRequested?.Invoke(this, photo);

    /// <summary>
    /// Ссылка на картинку (http/https с расширением изображения или data:image) вместо текста запроса — скачивается и открывается
    /// в студии; страница без картинки и прочий текст идут обычным запросом.
    /// </summary>
    private async Task<bool> TryOpenPhotoLinkAsync(string text)
    {
        if (text.Contains(' ') || !PhotoImageLoader.LooksLikeUrl(text)) return false;
        bool imageLike = text.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) ||
                         new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" }.Any(ext => text.Split('?', '#')[0].EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        if (!imageLike) return false;
        IsBusy = true;
        BusyText = Loc.Get("W4Loading");
        try
        {
            RequestPhotoStudio(await PhotoImageLoader.FromUrlAsync(text));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or ArgumentException or TaskCanceledException)
        {
            ShowError(Loc.Get("W4StudioTitle"), Loc.Format("W4LoadError", ex.Message), "AIA-PHOTO-URL", ex.ToString());
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
        return true;
    }

    // ───────────────────────────── Фото-референс ─────────────────────────────

    /// <summary>Исходный снимок поднятого здания (карточка в углу 3D-вьюпорта).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPhotoReferenceVisible))]
    public partial byte[]? PhotoReference { get; set; }

    [ObservableProperty] public partial string PhotoReferenceTitle { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPhotoReferenceVisible))]
    public partial bool IsPhotoReferenceHidden { get; set; }

    public bool IsPhotoReferenceVisible => PhotoReference is { Length: > 0 } && !IsPhotoReferenceHidden;

    [RelayCommand]
    private void HidePhotoReference() => IsPhotoReferenceHidden = true;

    private void PinPhotoReference(byte[] image, string title)
    {
        PhotoReference = image;
        PhotoReferenceTitle = title;
        IsPhotoReferenceHidden = false;
    }

    /// <summary>Нагрузки проекта для проверки каркаса поднятого здания.</summary>
    public StructuralLoadSettings PhotoLoads => Settings.Current.StructuralLoads.Clone();

    /// <summary>Подъём Визуальным ядром (без изменения сцены).</summary>
    public PhotoLiftResult LiftPhoto(PhotoArchitectureDescriptor descriptor, PhotoLiftOptions options, bool verify = true) =>
        PhotoToBimService.Lift(descriptor, options, PhotoLoads, verify);

    /// <summary>Вставка поднятого здания: новая сцена или «+ К участку» — одним шагом Undo, сообщение со сводкой, фото-референс.</summary>
    public void InsertPhotoResult(PhotoLiftResult result, byte[] image, string name)
    {
        var model = result.Model;
        if (model.Elements.Count == 0) return;
        if (IsAppendToSite && Scene.Elements.Count > 0)
            Scene.AppendBuildings(model.Groups, model.Elements.Select(e => e.Clone()).ToList(), null, model.Axes);
        else
        {
            Scene.LoadScene(model.Elements.Select(e => e.Clone(keepId: true)).ToList(), model.Groups.Select(g => g.Clone()).ToList(), recordUndo: Scene.Elements.Count > 0,
                axes: model.Axes.Select(a => a.Clone()).ToList(), dimensions: []);
            Scene.ProjectName = Path.GetFileNameWithoutExtension(name);
        }
        var report = new SolverReport { Success = true, ElapsedMilliseconds = result.ElapsedMs, InputElements = 1, GeneratedElements = model.Elements.Count, Actions = [.. result.Log] };
        if (!result.IsStructurallySound) report.Warnings.Add(PhotoToBimService.Summary(result));
        var message = ChatMessageModel.Assistant(Loc.Format("W4Inserted", name, PhotoToBimService.Summary(result)), Loc.Get("W4Engine"), report);
        message.Badge = result.IsStructurallySound ? Loc.Format("ReviewBadgeOk", Services.Verification.VerificationText.K(result.MaxUtilization), 0) : Loc.Get("PlanBadgeIssues");
        message.BadgeColorHex = result.IsStructurallySound ? "#30D158" : "#FF9F0A";
        message.Thumbnails = [image];
        ChatMessages.Add(message);
        VerificationBadgeText = message.Badge;
        VerificationBadgeColorHex = message.BadgeColorHex;
        PinPhotoReference(image, name);
        ShowSolverReport(report);
        ZoomExtentsRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Подъём выбранным маршрутом. Авто — основная локальная модель с паспортом снимка (если локальное ядро доступно), иначе
    /// Визуальное ядро; Совет КБ и веб-проектировщик получают снимок и VisionPromptV2; ответ любой модели сверяется со снимком
    /// и доводится в солвере (<see cref="AiRequestContext.Photo"/>).
    /// </summary>
    public async Task RunPhotoLiftAsync(PhotoLiftRoute route, PhotoArchitectureDescriptor descriptor, PhotoLiftOptions options, byte[] jpeg, string name, PhotoLiftResult? visualCore)
    {
        var primary = ModelDirector.Primary is { Available: true } p ? p.Option : null;
        if (route is PhotoLiftRoute.Auto or PhotoLiftRoute.PrimaryLocal && (primary is null || !IsLocalCoreReady(primary)))
        {
            if (route == PhotoLiftRoute.PrimaryLocal) ChatMessages.Add(ChatMessageModel.System(Loc.Get("W4NoLocal")));
            InsertPhotoResult(visualCore ?? await Task.Run(() => LiftPhoto(descriptor, options)), jpeg, name);
            return;
        }
        var model = route switch
        {
            PhotoLiftRoute.Web => SelectedModel.IsWeb ? SelectedModel : AiModelCatalog.WebOptions.First(),
            PhotoLiftRoute.Council => SelectedModel,
            _ => primary!
        };
        var dispatch = UniversalVisionModelBridge.Prepare(model, descriptor, jpeg, null);
        var context = UniversalVisionModelBridge.CreateContext(_ai, dispatch, descriptor, options, PhotoLoads, IsAppendToSite);
        var user = ChatMessageModel.User($"[{Loc.Get("W4StudioTitle")}] {name}");
        user.Thumbnails = [jpeg];
        ChatMessages.Add(user);
        ChatMessages.Add(ChatMessageModel.System(Loc.Format("W4SentToModel", name, route == PhotoLiftRoute.Council ? Loc.Get("W4RouteCouncil") : ModelLabel(model, shortForm: true), dispatch.Channel)));
        PinPhotoReference(jpeg, name);
        if (route == PhotoLiftRoute.Council)
        {
            await RunCouncilDesignAsync(context);
            return;
        }
        if (model.IsWeb)
        {
            WebBridgeSite = model.Provider;
            IsWebBridgeOpen = true;
            WebBridge.Prepare(context, _ai.BuildWebPrompt(context));
            return;
        }
        _aiCts?.Dispose();
        _aiCts = new CancellationTokenSource();
        IsBusy = true;
        BusyText = Loc.Format(model.IsOnDevice ? "BusyLocal" : "BusyCloud", ModelLabel(model, shortForm: true));
        try
        {
            OnAiSucceeded(await _ai.RunAsync(context, _aiCts.Token), IsExternalGenerator(model));
        }
        catch (OperationCanceledException)
        {
            ChatMessages.Add(ChatMessageModel.System(Loc.Get("AiCancelled")));
        }
        catch (Exception ex) when (ex is AiProviderException or HttpRequestException or InvalidOperationException)
        {
            // Модель недоступна или ответ не разобран — здание поднимает Визуальное ядро (результат пользователь видит сразу).
            ShowError(Loc.Get("ErrorAi"), ex.Message, ex is AiProviderException ape ? ape.Code : "AIA-PHOTO-MODEL", ex.ToString());
            InsertPhotoResult(visualCore ?? await Task.Run(() => LiftPhoto(descriptor, options)), jpeg, name);
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    /// <summary>Локальное ядро отвечает: модель сервера (после запуска) или встроенная модель с файлом весов.</summary>
    private static bool IsLocalCoreReady(AiModelOption model) => model.IsOnDevice;
}
