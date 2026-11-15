namespace KuniWhatsThisAsset.Core;
public static class ModelRenderPolicy
{
    public static bool Approve(ModelStatistics statistics, Func<ModelStatistics, bool> confirm) => !statistics.RequiresLoadWarning || confirm(statistics);
}
