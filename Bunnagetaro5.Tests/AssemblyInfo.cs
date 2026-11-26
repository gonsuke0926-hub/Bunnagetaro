using Xunit;

// AppLogとConfigStoreはプロセス単位の状態を持つ（AppLog.Initなど）。
// テスト間で干渉させないため並列実行せず逐次で回す
[assembly: CollectionBehavior(DisableTestParallelization = true)]
