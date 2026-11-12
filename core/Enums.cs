namespace SpeechToText;

public enum SttMode { Off, Continuous, Triggered }

public enum SttModel { TinyEn, BaseEn, SmallEn, Base, Small, LargeV3Turbo }

public enum SttBackend { Cpu, Vulkan, Cuda }

public enum VadKind { Silero, Energy }

/// <summary>Whose writes to <c>User/STT.Trigger</c> are honoured.</summary>
public enum TriggerSource { OnlyMe, Anyone }
