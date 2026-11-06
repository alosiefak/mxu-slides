namespace MxU.Slides.Core.Tests.MotionTemplates;

/// <summary>
/// A hand-written Lottie file with one of everything the app reads: a text layer with After
/// Effects' carriage returns, one inside a precomposition, a shape with a fill, a stroke and a
/// gradient, a moving colour, a solid, an effect's colour, a picture, fonts and the three markers
/// (the Out one written the LottieFiles way, as JSON).
/// </summary>
internal static class LottieSamples
{
    public const string Strap = """
    {
      "v": "5.7.4", "fr": 30, "ip": 0, "op": 150, "w": 1920, "h": 1080, "nm": "Speaker Strap", "ddd": 0,
      "fonts": { "list": [ { "fName": "Montserrat-Bold", "fFamily": "Montserrat", "fStyle": "Bold", "ascent": 72 } ] },
      "markers": [
        { "cm": "Hold", "tm": 20, "dr": 100 },
        { "cm": "In", "tm": 0, "dr": 20 },
        { "cm": "{\"name\":\"Out\"}", "tm": 120, "dr": 30 }
      ],
      "assets": [
        { "id": "image_0", "w": 200, "h": 100, "u": "", "p": "data:image/png;base64,AAAA", "e": 1 },
        { "id": "comp_0", "layers": [
          { "ty": 5, "nm": "Church", "ind": 1, "ip": 0, "op": 150, "st": 0, "ks": {},
            "t": { "d": { "k": [ { "s": { "s": 30, "f": "Montserrat-Bold", "t": "Grace Chapel", "fc": [1, 1, 1] }, "t": 0 } ] } } }
        ] }
      ],
      "layers": [
        { "ty": 5, "nm": "Speaker Name", "ind": 1, "ip": 0, "op": 150, "st": 0, "ks": {},
          "t": { "d": { "k": [ { "s": { "s": 60, "f": "Montserrat-Bold", "t": "Pastor Grace\rOkafor", "fc": [1, 1, 1], "sc": [0.0588, 0.1216, 0.2275] }, "t": 0 } ] } } },
        { "ty": 5, "nm": "Speaker Name", "ind": 2, "ip": 0, "op": 150, "st": 0, "ks": {},
          "t": { "d": { "k": [ { "s": { "s": 30, "f": "Montserrat-Bold", "t": "Lead Pastor", "fc": [0.9608, 0.7176, 0] }, "t": 0 } ] } } },
        { "ty": 4, "nm": "Plate", "ind": 3, "ip": 0, "op": 150, "st": 0, "ks": {},
          "shapes": [ { "ty": "gr", "nm": "Group", "it": [
            { "ty": "rc", "s": { "a": 0, "k": [800, 120] }, "p": { "a": 0, "k": [0, 0] }, "r": { "a": 0, "k": 0 } },
            { "ty": "fl", "c": { "a": 0, "k": [0.0588, 0.1216, 0.2275, 1] }, "o": { "a": 0, "k": 100 } },
            { "ty": "st", "c": { "a": 0, "k": [0.9608, 0.7176, 0, 1] }, "o": { "a": 0, "k": 100 }, "w": { "a": 0, "k": 2 } },
            { "ty": "gf", "o": { "a": 0, "k": 100 }, "t": 1, "g": { "p": 2, "k": { "a": 0, "k": [0, 1, 0, 0, 1, 0, 0, 1, 0, 1, 1, 1] } } }
          ] } ] },
        { "ty": 4, "nm": "Pulse", "ind": 4, "ip": 0, "op": 150, "st": 0, "ks": {},
          "shapes": [ { "ty": "fl", "c": { "a": 1, "k": [ { "t": 0, "s": [0.0588, 0.1216, 0.2275, 1] }, { "t": 30, "s": [1, 1, 1, 1] } ] } } ] },
        { "ty": 1, "nm": "Backing", "ind": 5, "ip": 0, "op": 150, "st": 0, "ks": {}, "sc": "#336699", "sw": 1920, "sh": 200 },
        { "ty": 4, "nm": "Glow", "ind": 6, "ip": 0, "op": 150, "st": 0, "ks": {}, "shapes": [],
          "ef": [ { "nm": "Tint", "ef": [ { "ty": 2, "nm": "Map White To", "v": { "a": 0, "k": [0.2, 0.4, 0.6, 1] } } ] } ] },
        { "ty": 2, "nm": "Logo", "ind": 7, "ip": 0, "op": 150, "st": 0, "ks": {}, "refId": "image_0" },
        { "ty": 0, "nm": "Church Group", "ind": 8, "ip": 0, "op": 150, "st": 0, "ks": {}, "refId": "comp_0", "w": 1920, "h": 1080 }
      ]
    }
    """;
}
