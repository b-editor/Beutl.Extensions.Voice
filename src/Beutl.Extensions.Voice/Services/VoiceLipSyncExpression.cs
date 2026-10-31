using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Beutl.Animation;
using Beutl.Engine;
using Beutl.Engine.Expressions;
using Beutl.Media;
using Beutl.Media.Source;

namespace Beutl.Extensions.Voice.Services;

// The expressions are evaluated against the current scene, so edits need no editor-only subscriptions.
// The group is independent of object IDs: both halves of a split keep following the same dialogue.
public abstract class VoiceLipSyncExpression<T> : IExpression<T>
{
    private IExpression<T>? _replaced;
    public Guid GroupId { get; init; }
    public JsonNode? PreviousExpression { get; init; }
    [JsonIgnore] public string ExpressionString => "読み上げの口パク連携";
    [JsonIgnore] public IExpression<T>? Replaced => _replaced ??= PreviousExpression == null ? null : Expression.CreateFromNode<T>(PreviousExpression);
    public bool Validate(out string? error) { error = null; return true; }
    public abstract T Evaluate(ExpressionContext context);
}

public sealed class VoiceLipSyncAudioExpression : VoiceLipSyncExpression<SoundSource?>
{
    public override SoundSource? Evaluate(ExpressionContext context)
    {
        if (PsdLipSync.FindSound(context, GroupId) is not { } sound) return null;
        var property = context.CurrentProperty;
        context.CurrentProperty = sound.Source;
        try { return sound.Source.Evaluate(context); }
        finally { context.CurrentProperty = property; }
    }
}

public sealed class VoiceLipSyncOffsetExpression : VoiceLipSyncExpression<MediaTime>
{
    // Weak keys let deleted clips and their managed integration caches be collected together.
    private readonly ConditionalWeakTable<KeyFrameAnimation<float>, SpeedIntegrator> _integrators = new();

    public override MediaTime Evaluate(ExpressionContext context)
    {
        if (PsdLipSync.FindSound(context, GroupId) is not { } sound
            || context.CurrentProperty.GetOwnerObject() is not EngineObject target) return MediaTime.Zero;
        return context.Time - target.TimeRange.Value.Start - PsdLipSync.SourceTime(sound, context.Time,
            animation => _integrators.GetValue(animation, value =>
            {
                var integrator = new SpeedIntegrator(48000);
                integrator.EnsureCache(value);
                return integrator;
            }));
    }
}
