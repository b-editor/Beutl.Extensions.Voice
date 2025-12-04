# AudioQuery UI Implementation

This document describes the implementation of the AudioQuery UI feature for adjusting voice accents and other parameters in the Beutl Voice Extension.

## Overview

The implementation follows the workflow specified in the requirements:

1. User inputs text
2. Generate AudioQuery
3. Parse AudioQuery
4. Display in UI
5. User adjusts accent, pitch and other parameters
6. Reflect changes in AudioQuery
7. Generate audio

## Architecture

### Models (Created)

#### AudioQuery.cs
Represents the audio synthesis query with the following properties:
- `AccentPhrases`: Array of accent phrases
- `SpeedScale`: Overall speech speed (0.5-2.0)
- `PitchScale`: Overall pitch adjustment (-0.15 to 0.15)
- `IntonationScale`: Overall intonation (0.0-2.0)
- `VolumeScale`: Overall volume (0.0-2.0)
- `PrePhonemeLength`: Silence before audio (seconds)
- `PostPhonemeLength`: Silence after audio (seconds)
- `OutputSamplingRate`: Audio sampling rate
- `OutputStereo`: Stereo output flag
- `Kana`: AquesTalk-style notation (read-only)

#### AccentPhrase.cs
Represents an accent phrase with:
- `Moras`: Array of mora (syllable units)
- `Accent`: Accent position (1-indexed)
- `IsInterrogative`: Whether it's a question
- `PauseMora`: Optional pause mora after the phrase

#### Mora.cs
Represents a mora (smallest speech unit) with:
- `Text`: Display text
- `Consonant`: Consonant phoneme
- `ConsonantLength`: Consonant duration (seconds)
- `Vowel`: Vowel phoneme
- `VowelLength`: Vowel duration (seconds)
- `Pitch`: Pitch in Hz

### ViewModels (Created/Modified)

#### AccentPhraseViewModel.cs
Wraps AccentPhrase for UI binding with:
- `Accent`: Reactive property for accent position
- `IsInterrogative`: Reactive property for question flag
- `Moras`: Observable collection of MoraViewModel
- Two-way binding that updates the underlying model

#### MoraViewModel.cs
Wraps Mora for UI binding with:
- `Text`: Display text (read-only in UI)
- `Pitch`: Reactive property for pitch adjustment
- `VowelLength`: Reactive property for duration adjustment
- Two-way binding that updates the underlying model

#### TtsTabViewModel.cs (Modified)
Added new properties and methods:
- `CurrentAudioQuery`: Stores the generated AudioQuery
- `IsAudioQueryGenerated`: Flag indicating if AudioQuery is available
- `AccentPhrases`: Observable collection for UI binding
- `GenerateAudioQuery()`: New method to generate AudioQuery from text
- Modified `Tts()`: Now uses AudioQuery if available, falls back to direct TTS otherwise

### Views (Modified)

#### TtsTabView.axaml
Enhanced UI with:

1. **New "AudioQuery生成" button**: Generates AudioQuery from text
2. **AudioQuery editor section**: Shows when AudioQuery is generated
3. **Global parameter sliders**:
   - Speech speed (話速): 0.5-2.0
   - Pitch (音高): -0.15 to 0.15
   - Intonation (抑揚): 0.0-2.0
   - Volume (音量): 0.0-2.0
   - Pre-silence (前の無音): 0.0-1.5 seconds
   - Post-silence (後の無音): 0.0-1.5 seconds
4. **Accent phrase editor**:
   - Displays each accent phrase with its text
   - Accent position selector (NumericUpDown)
   - Question mark checkbox
   - Per-mora pitch adjustment controls
5. **Existing buttons** ("追加" and "読み上げ"): Now use AudioQuery when available

## Workflow

### Without AudioQuery (Original behavior)
1. User enters text
2. Selects voice and style
3. Clicks "追加" or "読み上げ"
4. System directly generates audio using TTS API

### With AudioQuery (New behavior)
1. User enters text
2. Selects voice and style
3. Clicks "AudioQuery生成"
4. System calls `CreateAudioQuery` API
5. AudioQuery is parsed and displayed in UI
6. User adjusts parameters:
   - Global parameters (speed, pitch, intonation, volume)
   - Per-phrase accent position
   - Per-phrase question flag
   - Per-mora pitch values
7. User clicks "追加" or "読み上げ"
8. System uses `Synthesis` API with modified AudioQuery
9. Audio is generated with customized parameters

## Technical Details

### JSON Serialization
The AudioQuery models use `System.Text.Json` with `JsonPropertyName` attributes to match the VOICEVOX API schema. The serialization handles snake_case and camelCase property names correctly.

### Reactive Programming
The implementation uses ReactiveBindings extensively:
- Changes to sliders immediately update the AudioQuery model
- ObservableCollection automatically updates the UI when accent phrases change
- Two-way bindings ensure UI and model stay synchronized

### API Integration
The implementation uses the VoicevoxCoreSharp library:
- `Synthesizer.CreateAudioQuery()`: Generates AudioQuery from text
- `Synthesizer.Synthesis()`: Synthesizes audio from AudioQuery
- `Synthesizer.Tts()`: Direct text-to-speech (fallback)

## Future Enhancements

Possible improvements:
1. Pitch visualization graph
2. Audio waveform preview
3. Save/load AudioQuery presets
4. Batch processing multiple AudioQueries
5. Advanced phoneme editing
6. Undo/redo for parameter changes
7. Visual accent position indicator
8. Mora duration adjustment UI
