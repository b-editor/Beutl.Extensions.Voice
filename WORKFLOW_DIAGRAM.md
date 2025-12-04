# AudioQuery Workflow Diagram

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         User Interaction Flow                            │
└─────────────────────────────────────────────────────────────────────────┘

┌──────────────────┐
│ User enters text │
│ Selects voice    │
│ Selects style    │
└────────┬─────────┘
         │
         ▼
┌─────────────────────────────────┐
│ Click "AudioQuery生成" button    │
└────────┬────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────────────────────────────┐
│ TtsTabViewModel.GenerateAudioQuery()                             │
│  • Calls synthesizer.CreateAudioQuery()                          │
│  • Receives JSON response                                        │
│  • Deserializes to AudioQuery model                             │
│  • Creates AccentPhraseViewModel for each phrase                │
│  • Sets IsAudioQueryGenerated = true                            │
└────────┬─────────────────────────────────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────────────────────────────┐
│ UI displays AudioQuery editor                                    │
│                                                                  │
│ ┌────────────────────────────────────────────────────────────┐ │
│ │ Global Parameters                                          │ │
│ │  • Speed Scale      [====●====]  1.00                      │ │
│ │  • Pitch Scale      [====●====]  0.00                      │ │
│ │  • Intonation Scale [====●====]  1.00                      │ │
│ │  • Volume Scale     [====●====]  1.00                      │ │
│ │  • Pre Silence      [====●====]  0.10s                     │ │
│ │  • Post Silence     [====●====]  0.10s                     │ │
│ └────────────────────────────────────────────────────────────┘ │
│                                                                  │
│ ┌────────────────────────────────────────────────────────────┐ │
│ │ Accent Phrases                                             │ │
│ │ ┌────────────────────────────────────────────────────────┐ │ │
│ │ │ Phrase 1: "こんにちは"                                  │ │ │
│ │ │ Accent Position: [3▼]  ☐ Interrogative                 │ │ │
│ │ │ Moras: [こ:P120] [ん:P110] [に:P130] [ち:P100] [は:P90] │ │ │
│ │ └────────────────────────────────────────────────────────┘ │ │
│ │ ┌────────────────────────────────────────────────────────┐ │ │
│ │ │ Phrase 2: "世界"                                       │ │ │
│ │ │ Accent Position: [1▼]  ☐ Interrogative                 │ │ │
│ │ │ Moras: [せ:P140] [かい:P95]                             │ │ │
│ │ └────────────────────────────────────────────────────────┘ │ │
│ └────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────┘
         │
         ▼
┌────────────────────────────┐
│ User adjusts parameters    │
│  • Move sliders            │
│  • Change accent positions │
│  • Adjust mora pitches     │
└────────┬───────────────────┘
         │ (Changes are immediately reflected
         │  in AudioQuery model via reactive bindings)
         ▼
┌─────────────────────────────────────┐
│ Click "追加" or "読み上げ" button     │
└────────┬────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────────────────────────────┐
│ TtsTabViewModel.Tts()                                            │
│  • Checks if CurrentAudioQuery is available                      │
│  • If YES: Serializes AudioQuery to JSON                         │
│           Calls synthesizer.Synthesis(json)                      │
│  • If NO:  Calls synthesizer.Tts(text) directly                  │
│  • Returns audio bytes                                           │
└────────┬─────────────────────────────────────────────────────────┘
         │
         ▼
┌────────────────────────────────────┐
│ TtsTabViewModel.Generate() or      │
│ TtsTabViewModel.Play()             │
│  • Receives audio bytes            │
│  • Writes to WAV file (Generate)   │
│  • Or plays directly (Play)        │
└────────────────────────────────────┘


┌─────────────────────────────────────────────────────────────────────────┐
│                         Data Flow Diagram                                │
└─────────────────────────────────────────────────────────────────────────┘

User Input                  VOICEVOX API              Models
    │                            │                        │
    │ Text + Voice + Style       │                        │
    ├──────────────────────────►│                        │
    │                            │ CreateAudioQuery       │
    │                            │────────────────────┐   │
    │                            │                    ▼   │
    │                            │              AudioQuery │
    │                            │                JSON     │
    │                            │◄───────────────────┘   │
    │                            │                        │
    │◄───────────────────────────┤                        │
    │                            │                        │
    │ Deserialize               │                        │
    ├───────────────────────────────────────────────────►│
    │                            │                  AudioQuery
    │                            │                  AccentPhrase[]
    │                            │                  Mora[]
    │                            │                        │
    │ Create ViewModels          │                        │
    ├───────────────────────────────────────────────────►│
    │                            │         AccentPhraseViewModel
    │                            │         MoraViewModel
    │                            │                        │
    │ User edits parameters      │                        │
    │◄──────────────────────────────────────────────────►│
    │ (Two-way reactive bindings)│                        │
    │                            │                        │
    │ Serialize to JSON          │                        │
    ├──────────────────────────►│                        │
    │                            │                        │
    │ Modified AudioQuery JSON   │                        │
    ├──────────────────────────►│                        │
    │                            │ Synthesis              │
    │                            │────────────────────┐   │
    │                            │                    ▼   │
    │                            │              Audio WAV  │
    │◄───────────────────────────┤                        │
    │                            │                        │
    ▼                            │                        │
Audio Output                     │                        │
(Play or Timeline)               │                        │


┌─────────────────────────────────────────────────────────────────────────┐
│                      Component Relationships                             │
└─────────────────────────────────────────────────────────────────────────┘

┌───────────────────┐
│   TtsTabView      │ (View - XAML)
│   .axaml          │
└─────────┬─────────┘
          │ Data Bindings
          ▼
┌───────────────────────────────────────────────────────┐
│          TtsTabViewModel                              │
│  • Text                                               │
│  • SelectedVoice                                      │
│  • SelectedStyle                                      │
│  • CurrentAudioQuery ◄────────┐                      │
│  • AccentPhrases ◄───────┐    │                      │
│  • GenerateAudioQuery()  │    │                      │
│  • Generate()            │    │                      │
│  • Play()                │    │                      │
└──────────────────────────┼────┼───────────────────────┘
                           │    │
                ┌──────────┘    │
                │               │
                ▼               │
┌──────────────────────────┐   │
│ AccentPhraseViewModel    │   │
│  • Model ──────────────────┐ │
│  • Accent (reactive)     │ │ │
│  • IsInterrogative       │ │ │
│  • Moras                 │ │ │
│  • Dispose()             │ │ │
└──────────┬───────────────┘ │ │
           │                 │ │
           ▼                 │ │
┌──────────────────────────┐ │ │
│ MoraViewModel            │ │ │
│  • Model ──────────────┐ │ │ │
│  • Pitch (reactive)    │ │ │ │
│  • VowelLength        │ │ │ │
│  • Dispose()          │ │ │ │
└───────────────────────┼┘ │ │ │
                        │  │ │ │
                        ▼  ▼ ▼ ▼
            ┌─────────────────────────────────┐
            │         Models                  │
            │  • AudioQuery                   │
            │  • AccentPhrase                 │
            │  • Mora                         │
            └──────────────┬──────────────────┘
                           │ JSON Serialization
                           ▼
            ┌─────────────────────────────────┐
            │     VoicevoxCoreSharp           │
            │  • Synthesizer.CreateAudioQuery │
            │  • Synthesizer.Synthesis        │
            │  • Synthesizer.Tts              │
            └─────────────────────────────────┘
```
