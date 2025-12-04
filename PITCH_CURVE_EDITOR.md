# Drag-Based Pitch Curve Editor

## Overview

The pitch curve editor provides an intuitive, visual way to adjust the pitch of individual moras (syllables) by dragging points on a graph.

## Visual Layout

```
┌────────────────────────────────────────────────────────────────┐
│ ピッチカーブ（ドラッグで編集）:                                 │
├────────────────────────────────────────────────────────────────┤
│                                                                │
│ 200Hz ┌─────────────────────────────────────────────────────┐ │
│       │ - - - - - - - - - - - - - - - - - - - - - - - - - │ │
│       │                     ●130                           │ │
│ 150Hz │ - - - - - - - - - - | - - - - - - - - - - - - - │ │
│       │         ●120        |        ●100                 │ │
│       │        /  \         |       /   \                 │ │
│ 100Hz │ - - -/- - -\- - - - - - - /- - -\- - - - - - - │ │
│       │     /       \              /     \   ●90          │ │
│       │ ●110         \            /       \ /  \          │ │
│  50Hz │ - - - - - - -●95- - - - - - - - -●- - -\- - - │ │
│       │                                           \       │ │
│   0Hz └─────────────────────────────────────────────────●───┘ │
│       こ   ん    に    ち    は    せ   か   い         │
│       P:110 P:120 P:95 P:130 P:100 P:140 P:90 P:85      │
└────────────────────────────────────────────────────────────────┘
```

## Features

### Interactive Drag Control
- **Click and drag** any point vertically to adjust pitch
- Points are connected with smooth lines showing pitch curve
- Real-time visual feedback as you drag

### Visual Elements
1. **Grid Lines** - Horizontal reference lines for pitch values
2. **Pitch Curve** - Blue line connecting all mora points
3. **Draggable Points** - Circular markers for each mora
4. **Mora Labels** - Text below each point showing the syllable
5. **Pitch Values** - Numbers above each point showing current pitch

### User Interaction

```
┌─────────────────────────────────────────────────────────┐
│ 1. Mouse Over                                           │
│    Point highlights when hovering                       │
│                                                         │
│    Normal: ●  (Blue)                                   │
│    Hover:  ● (Light Blue)                              │
│    Drag:   ● (Light Blue + moves with mouse)           │
└─────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────┐
│ 2. Click on Point                                       │
│    - Click directly on a point (● marker)              │
│    - Point becomes "active" and follows mouse           │
└─────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────┐
│ 3. Drag Vertically                                      │
│    - Move mouse up: Increase pitch                      │
│    - Move mouse down: Decrease pitch                    │
│    - Pitch value updates in real-time                   │
│    - Curve redraws automatically                        │
└─────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────┐
│ 4. Release                                              │
│    - Release mouse button to finish editing            │
│    - New pitch value is saved to model                 │
│    - Point returns to normal state                     │
└─────────────────────────────────────────────────────────┘
```

## Technical Implementation

### Component: PitchCurveEditor.cs

**Key Properties:**
```csharp
- Moras: ObservableCollection<MoraViewModel>
  Collection of mora view models to edit

- MinPitch: double (default: 0)
  Minimum pitch value in Hz

- MaxPitch: double (default: 200)
  Maximum pitch value in Hz
```

**Interaction Flow:**
```
OnPointerPressed
    ↓
Find clicked point
    ↓
Set _draggedIndex
    ↓
OnPointerMoved
    ↓
Calculate new pitch from Y position
    ↓
Update Moras[_draggedIndex].Pitch.Value
    ↓
InvalidateVisual (redraw)
    ↓
OnPointerReleased
    ↓
Clear _draggedIndex
```

**Rendering Logic:**
1. Draw background with grid
2. Calculate point positions based on pitch values
3. Draw lines connecting points
4. Draw circular markers at each point
5. Draw text labels below (mora text)
6. Draw pitch values above each point

## Integration with XAML

```xml
<views:PitchCurveEditor Height="150"
                        Margin="0,4,0,0"
                        Moras="{Binding Moras}"
                        MinPitch="0"
                        MaxPitch="200" />
```

The editor binds to the `Moras` collection of the current AccentPhraseViewModel.

## Advantages Over NumericUpDown

### Before (NumericUpDown)
```
モーラごとのピッチ:
┌────┐ ┌────┐ ┌────┐ ┌────┐ ┌────┐
│ こ │ │ ん │ │ に │ │ ち │ │ は │
│P:120│ │P:110│ │P:95 │ │P:130│ │P:100│
└────┘ └────┘ └────┘ └────┘ └────┘
```
- Requires clicking up/down buttons or typing
- No visual context of pitch curve
- Hard to see overall pattern
- Tedious for fine adjustments

### After (Drag-Based Curve)
```
ピッチカーブ（ドラッグで編集）:
     ●
    / \
   /   \●
  ●     
 こ ん に ち は
```
- Visual representation of pitch curve
- Drag to adjust instantly
- See overall pattern at a glance
- Smooth, natural editing experience
- All moras visible in context

## Use Cases

### 1. Creating Natural Intonation
Drag points to create smooth pitch curves that sound natural:
```
     ●
    / \
   /   \
  ●     ●
 こんにちは
```

### 2. Emphasizing Words
Raise specific moras to emphasize important words:
```
       ●●  ← Emphasis
      /  \
     /    \
    ●      ●
  大 切  な  話
```

### 3. Question Intonation
Create rising intonation at end of questions:
```
             ●  ← Rising
            /
           /
    ●●●●●●
  これでいいですか？
```

### 4. Flat Pronunciation
Adjust all points to same level for flat reading:
```
  ●●●●●●●
  平坦な読み方
```

## Future Enhancements

Possible improvements:
1. **Smooth Curve Mode** - Automatic curve smoothing
2. **Multi-Select** - Drag multiple points at once
3. **Bezier Curves** - Curved lines instead of straight
4. **Undo/Redo** - History of pitch changes
5. **Presets** - Save/load common pitch patterns
6. **Audio Preview** - Play audio while editing
7. **Zoom Controls** - Zoom in for precise editing
8. **Snap to Grid** - Snap to specific pitch values
