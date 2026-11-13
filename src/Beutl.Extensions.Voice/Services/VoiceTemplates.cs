using Beutl.Editor.Services;
using Beutl.Graphics;
using Beutl.ProjectSystem;

namespace Beutl.Extensions.Voice.Services;

// Speech takes its characters and captions from the template library, never from the timeline.
internal static class VoiceTemplates
{
    // The saved elements and drawables; each picker offers the ones it can use.
    public static IEnumerable<ObjectTemplateItem> Load() => ObjectTemplateService.Instance.FindByBaseType(typeof(Element))
        .Concat(ObjectTemplateService.Instance.FindByBaseType(typeof(Drawable)));

    // A saved drawable is wrapped in an element, so both kinds are searched the same way.
    public static Element? CreateElement(ObjectTemplateItem template)
    {
        // CreateInstance also resolves packaged template assets relative to the template file.
        var instance = template.CreateInstance();
        Element element;
        if (instance is Element savedElement) element = savedElement;
        else if (instance is Drawable drawable)
        {
            element = new Element { Length = { Value = MediaTime.FromSeconds(1) } };
            element.AddObject(drawable);
        }
        else return null;
        element.Name.Value = template.Name.Value;
        return element;
    }
}
