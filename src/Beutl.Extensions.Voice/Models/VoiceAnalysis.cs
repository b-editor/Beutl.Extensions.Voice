namespace Beutl.Extensions.Voice.Models;

// Keep an edited query associated with the text and voice that produced its phonemes.
public sealed record VoiceAnalysis(string Text, VoiceSettings Settings, AudioQueryModel AudioQuery);
