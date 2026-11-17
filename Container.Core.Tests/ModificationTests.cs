namespace Container_V3.Tests;

/// <summary>
/// Тесты изменения готового контейнера: добавление, замена по имени, удаление файла и папки
/// целиком, уплотнение и правило резервной копии («копия только до 100 МБ»).
///
/// Round-trip этого не показывает. Здесь проверяется то, на чём держится договорённость о
/// контейнерах в несколько гигабайт: добавление и удаление не переупаковывают файл, после
/// удаления контейнер не уменьшается, список чанков сохраняется целиком (номер чанка обязан
/// совпадать с его позицией — иначе распаковка возьмёт не тот чанк), а после compact
/// содержимое обязано остаться побайтово тем же.
///
/// Сам сценарий последовательный и живёт в ModificationScenario (общий фикстур на весь класс):
/// каждый следующий шаг опирается на состояние контейнера после предыдущего. Здесь только
/// проверка накопленных по шагам расхождений — так падение одного шага даёт понятный отчёт,
/// а не каскад из двенадцать провалов.
/// </summary>
public class ModificationTests(ModificationScenario scenario) : IClassFixture<ModificationScenario>
{
    private readonly ModificationScenario _scenario = scenario;

    private static void AssertStep(Dictionary<string, List<string>> failures, string step)
    {
        Assert.False(failures.TryGetValue(step, out var errors) && errors.Count > 0,
            $"шаг «{step}»: " + string.Join("; ", errors ?? []));
    }

    [Fact]
    public void Step1_CreateByAppending() => AssertStep(_scenario.Failures, "Step1_Create");

    [Fact]
    public void Step2_AppendReplacesByNameAndKeepsContainerAppendOnly() => AssertStep(_scenario.Failures, "Step2_Append");

    [Fact]
    public void Step3_RemoveDoesNotRewriteDataAndKeepsChunkNumbers() => AssertStep(_scenario.Failures, "Step3_Remove");

    [Fact]
    public void Step4_CompactShrinksFileWithoutChangingContent() => AssertStep(_scenario.Failures, "Step4_Compact");

    [Fact]
    public void Step5_AppendAndCompactAfterCompact() => AssertStep(_scenario.Failures, "Step5_AppendAfterCompact");

    [Fact]
    public void Step6_EmptyFolderSurvivesRemovingItsLastFile() => AssertStep(_scenario.Failures, "Step6_EmptyFolderAfterLastFile");

    [Fact]
    public void Step7_EmptyFolderRecordIsTheDeepestOne() => AssertStep(_scenario.Failures, "Step7_DeepestEmptyFolder");

    [Fact]
    public void Step8_RemoveEmptyFolderByName_ParentBecomesEmptyRecord() => AssertStep(_scenario.Failures, "Step8_RemoveEmptyFolders");

    [Fact]
    public void Step9_EmptyFolderRecordsSurviveCompact() => AssertStep(_scenario.Failures, "Step9_EmptyFoldersSurviveCompact");

    [Fact]
    public void Step10_ContentMaskRemovesFolderAndTheFolderItself() => AssertStep(_scenario.Failures, "Step10_ContentMask");

    [Fact]
    public void Step11_RefusingMasksLeaveContainerUntouched() => AssertStep(_scenario.Failures, "Step11_MaskRefusals");

    [Fact]
    public void Step12_RefusalsLeaveContainerUntouched() => AssertStep(_scenario.Failures, "Step12_Refusals");

    [Fact]
    public void BackupRule_OnlyUpToThreshold() => AssertStep(_scenario.Failures, "BackupRule");

    // Имена шагов, которые проверяет каждый тест ниже.
    private static readonly string[] Steps =
    [
        "Step1_Create", "Step2_Append", "Step3_Remove", "Step4_Compact", "Step5_AppendAfterCompact",
        "Step6_EmptyFolderAfterLastFile", "Step7_DeepestEmptyFolder", "Step8_RemoveEmptyFolders",
        "Step9_EmptyFoldersSurviveCompact", "Step10_ContentMask", "Step11_MaskRefusals",
        "Step12_Refusals", "BackupRule",
    ];

    [Fact]
    public void Scenario_RanEveryStep() =>
        Assert.Equal(Steps, _scenario.ExecutedSteps);

    // Ничего не должно «просочиться» мимо поимённой проверки шагов: падение сценария
    // целиком или ошибка с незнакомым именем шага иначе остались бы незамеченными.
    [Fact]
    public void Scenario_HasNoUnattributedFailures()
    {
        var stray = _scenario.Failures
            .Where(kv => kv.Value.Count > 0 && Array.IndexOf(Steps, kv.Key) < 0)
            .ToList();

        Assert.True(stray.Count == 0,
            string.Join("; ", stray.Select(kv => $"шаг «{kv.Key}»: {string.Join("; ", kv.Value)}")));
    }
}
