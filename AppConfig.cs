namespace WhisperNote;

public static class AppConfig
{
    public const int ServerPort = 8082;
    public const int SampleRate = 16000;
    public const int BitsPerSample = 16;
    public const int Channels = 1;
    public const string GpuLayers = "all";
    public const int ContextSize = 16384;
    public const int BatchSize = 512;
    public const int UBatchSize = 256;
    public const double Temperature = 0.3;
    public const double TopP = 0.9;
    public const double MinP = 0.1;
    public const double RepeatPenalty = 1.2;
    public const int MaxTokens = 4096;
    public const int MicDeviceNumber = -1;

    public const string ServerExeRelative = @"llama\llama-server.exe";
}
