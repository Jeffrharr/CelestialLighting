using UnityEngine;
using Verse;

namespace CelestialLighting;

// The one-time "what's new" window: tells a player who already had this mod installed that §27
// vector lighting and §25c volumetric clouds have arrived, and offers to switch them on. The thin
// Verse adapter over UpdateNoticeMath — every decision about whether to appear, what to say about
// each feature and what the switches become lives there; this file draws it and writes the answer
// back. See DESIGN.md "Update notice".
//
// WHY A NOTICE AT ALL, for a mod whose whole settings screen is one scroll away. Both features ship
// in a state where a returning player would never find them. §27 ships OFF, on purpose — it is the
// most opinionated thing in the mod — so without being told, the only way to discover it is to read
// a checkbox list of thirty switches and notice one that was not there before. §25c ships ON, which
// is worse in the other direction: it changes what the sky looks like without ever being mentioned,
// and a player whose clouds suddenly render differently has no way to connect that to a setting.
//
// WHY IT IS NOT A `Dialog_MessageBox`. That class draws text and up to three buttons, so offering
// two independent features through it means either one button that takes both or a chain of two
// dialogs. Neither is what was asked for: the features are unrelated to each other and a player may
// well want the clouds and not the lighting. A checkbox each is the honest shape.
public class Dialog_UpdateNotice : Window
{
    // Not `= new(...)`: this is captured once at construction rather than re-read per frame, because
    // the offers must not change under the player's fingers while the window is open. Enabling a
    // feature elsewhere is impossible from here (the window is modal), but the shader-loaded and
    // bake-state reads behind it are not frozen, and a row that appeared as an offer and resolved as
    // "already on" would apply nothing while looking like it applied something.
    private readonly UpdateNoticeSwitches switches;

    private readonly UpdateNoticeOffer vectorLightOffer;
    private readonly UpdateNoticeOffer volumetricCloudOffer;

    // The tick state of the two checkboxes. Pre-ticked, deliberately: the notice exists to
    // surface features, the player has an explicit "Not now" that applies none of them, and a
    // pre-ticked box that they can clear is a smaller imposition than a window whose default answer
    // is "you saw nothing". Neither feature affects gameplay — both are visual only — so the cost of
    // an accepted default is a look the player can undo with one checkbox.
    private bool enableVectorLights = true;
    private bool enableVolumetricClouds = true;

    // Whether the player actually said yes, as opposed to the window merely ending.
    //
    // A SEPARATE FLAG BECAUSE THE BOXES ARE PRE-TICKED, and that combination is a trap without it:
    // the exit path is PostClose, which fires for every way out INCLUDING ones nobody chose —
    // Escape, another mod closing the stack, the test harness's blocking-dialog sweep. Reading the
    // tick state there would take all of those as consent and switch two features on for a player
    // who never pressed anything. So the ticks say what Apply WOULD do, and this says whether Apply
    // happened; everything else is a decline.
    private bool accepted;

    private Vector2 scrollPosition;
    private float contentHeight;

    private const float TitleHeight = 42f;
    private const float ButtonHeight = 35f;
    private const float ButtonGap = 10f;
    private const float ScrollBarWidth = 20f;

    public override Vector2 InitialSize => new Vector2(640f, 520f);

    public Dialog_UpdateNotice(UpdateNoticeSwitches switches)
    {
        this.switches = switches;
        vectorLightOffer = UpdateNoticeMath.VectorLightOffer(switches);
        volumetricCloudOffer = UpdateNoticeMath.VolumetricCloudOffer(switches);

        // forcePause is inert at the main menu (there is no game to pause) and correct if a future
        // caller ever raises this in-game. absorbInputAroundWindow keeps a click that misses the
        // window from landing on the main menu behind it.
        forcePause = true;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;

        // Escape and Enter both resolve the notice rather than deferring it — see OnCancelKeyPressed
        // below for why a dismissal has to count as an answer.
        closeOnAccept = false;
        closeOnCancel = false;
        forceCatchAcceptAndCancelEventEvenIfUnfocused = true;

        // One at a time. UIRoot_Entry.Init runs again every time the player returns to the main menu
        // from a game, and while the acknowledgement written in PostClose already prevents a second
        // one, this makes it structural rather than a property of the write having landed.
        onlyOneOfTypeAllowed = true;
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, inRect.y, inRect.width, TitleHeight), "Celestial Lighting — what's new");
        Text.Font = GameFont.Small;

        float top = inRect.y + TitleHeight;
        Rect outRect = new Rect(inRect.x, top, inRect.width, inRect.height - ButtonHeight - ButtonGap - top);
        Rect viewRect = new Rect(0f, 0f, outRect.width - ScrollBarWidth, Mathf.Max(contentHeight, outRect.height));

        Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);

        var listing = new Listing_Standard();
        // Same reason as the settings window: without this, Listing_Standard breaks into a second
        // column off to the right the moment the content passes the visible height, instead of
        // scrolling.
        listing.maxOneColumn = true;
        listing.Begin(viewRect);

        listing.Label("This update adds two new effects. Both are visual only — nothing here changes "
            + "plant growth, work speed, pawn vision or anything else the game reads a number from.");
        listing.Gap();

        DrawVectorLightRow(listing);
        listing.GapLine();
        DrawVolumetricCloudRow(listing);

        listing.Gap();
        listing.Label("Both switches live in Options → Mod settings → Celestial Lighting, alongside "
            + "the rest of them, if you would rather decide later or change your mind.");

        contentHeight = listing.CurHeight;
        listing.End();

        Widgets.EndScrollView();

        DrawButtons(inRect);
    }

    private void DrawVectorLightRow(Listing_Standard listing)
    {
        DrawFeature(listing, vectorLightOffer,
            heading: "Vector light sources",
            body: "Artificial light cast as a shape from each lamp rather than flooded outwards: a beam "
                + "through a doorway, a hard shadow behind a rock, firelight spilling out of a window. "
                + "Pawns throw a shadow away from every lamp lighting them.\n\n"
                + "The trade is that light which only reached a room by bending around a corner no "
                + "longer arrives, so indirectly lit rooms are genuinely darker than you are used to.",
            offerLabel: "Turn on vector light sources",
            ref enableVectorLights);
    }

    private void DrawVolumetricCloudRow(Listing_Standard listing)
    {
        DrawFeature(listing, volumetricCloudOffer,
            heading: "Volumetric clouds",
            body: "The drifting clouds are now lit by marching through a real three-dimensional model of "
                + "them instead of tinting a flat picture. Each cloud shadows its own underside, so a low "
                + "sun lights the tops while the bulk beneath stays dark, and the shape of that shading "
                + "changes through the day rather than just its brightness.\n\n"
                + "This one runs on the graphics card. If you are short of frames it is the first cloud "
                + "setting to turn off, and the clouds are still drawn the flat way without it.",
            offerLabel: "Turn on visible clouds, with volumetric lighting",
            ref enableVolumetricClouds);
    }

    // One row: heading, description, and then either a tickbox or a note that the feature is already
    // running. An Unavailable feature draws nothing at all — see UpdateNoticeOffer.Unavailable.
    private void DrawFeature(Listing_Standard listing, UpdateNoticeOffer offer, string heading, string body,
        string offerLabel, ref bool enable)
    {
        if (offer == UpdateNoticeOffer.Unavailable)
            return;

        Text.Font = GameFont.Medium;
        listing.Label(heading);
        Text.Font = GameFont.Small;
        listing.Label(body);

        if (offer == UpdateNoticeOffer.OfferToEnable)
        {
            listing.CheckboxLabeled(offerLabel, ref enable);
            return;
        }

        GUI.color = Color.gray;
        listing.Label("Already switched on — nothing to do.");
        GUI.color = Color.white;
    }

    private void DrawButtons(Rect inRect)
    {
        float y = inRect.height - ButtonHeight;

        // Nothing to offer means every new feature is either already running or unreachable here, so
        // the window is an announcement and gets one button. Drawing a disabled "Apply" instead would
        // be asking a question with no answers.
        if (!UpdateNoticeMath.AnyOffer(switches))
        {
            if (Widgets.ButtonText(new Rect(inRect.width / 2f - 100f, y, 200f, ButtonHeight), "OK"))
                Close();

            return;
        }

        float half = inRect.width / 2f;
        float width = half - ButtonGap;

        // Confirm on the right, dismiss on the left — the layout every vanilla Dialog_MessageBox
        // uses, so the muscle memory is right even though the widget is ours. "Not now" sets nothing:
        // declining is the default state of `accepted`, so it only has to close.
        if (Widgets.ButtonText(new Rect(0f, y, width, ButtonHeight), "Not now"))
            Close();

        if (Widgets.ButtonText(new Rect(half + ButtonGap, y, width, ButtonHeight), "Apply"))
        {
            accepted = true;
            Close();
        }
    }

    // Escape. Applies nothing, but PostClose still records the notice as answered — a player who
    // pressed Escape has decided, and showing them the same window on the next boot would read as
    // the mod nagging rather than as the mod being careful.
    public override void OnCancelKeyPressed()
    {
        Close();
        Event.current.Use();
    }

    // Enter is the keyboard form of the confirm button, so it means what that button means.
    public override void OnAcceptKeyPressed()
    {
        accepted = true;
        Close();
        Event.current.Use();
    }

    // THE SINGLE EXIT, and every way out of the window funnels through it — the two buttons, Escape,
    // Enter, and anything that closes the window stack from underneath us. That is why the
    // acknowledgement is written here rather than in the Apply handler: a notice that only records
    // itself on "yes" comes back every boot for everyone who said no, which is the exact failure the
    // "only shows once" requirement names.
    //
    // And it is why the tick state is gated on `accepted` (see that field): the acknowledgement has
    // to happen on every exit, the enabling must not.
    public override void PostClose()
    {
        base.PostClose();
        UpdateNotice.RecordAnswer(
            accepted && enableVectorLights,
            accepted && enableVolumetricClouds);
    }
}
