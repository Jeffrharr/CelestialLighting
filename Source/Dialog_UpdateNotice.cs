using UnityEngine;
using Verse;

namespace CelestialLighting;

// The one-time "what's new" window: tells a player who already had this mod installed that vector
// lighting and volumetric clouds have arrived, and gives them one button to switch vector lighting
// on. The thin Verse adapter over UpdateNoticeMath — every decision about whether to appear and what
// to say about each feature lives there; this file draws it and writes the answer back. See
// DESIGN.md "Update notice".
//
// WHY A NOTICE AT ALL, for a mod whose whole settings screen is one scroll away. Both features ship
// in a state where a returning player would never find them. Vector lighting stays OFF on update —
// see UpdateNoticeMath.FirstRunSwitches for why an upgrade and a new install deliberately differ —
// so without being told, the only route to it is reading a list of thirty checkboxes and noticing
// one that was not there before. Volumetric clouds are the opposite: they arrive already running and
// change what the sky looks like without ever being mentioned, so a player whose clouds suddenly
// render differently has no way to connect that to a setting.
//
// ONE BUTTON, NOT A TICKBOX PER FEATURE. Only one of the two is offerable at all — the cloud row is
// announcement-only by design (UpdateNoticeMath.VolumetricCloudRow) — so a checkbox plus an Apply
// button would be two clicks and a moment's reading to express one yes. It is also the more honest
// shape for the thing being asked: vector lighting is the largest visual change in the mod, and a
// button that says what it does is a clearer commitment than a pre-ticked box the player might not
// register having agreed to.
public class Dialog_UpdateNotice : Window
{
    // Captured once at construction rather than re-read per frame, because the rows must not change
    // under the player's fingers while the window is open. Enabling a feature elsewhere is
    // impossible from here (the window is modal), but the shader-loaded read behind the cloud row is
    // not frozen, and a row that appeared as an offer and resolved as "already on" would apply
    // nothing while looking like it applied something.
    private readonly UpdateNoticeSwitches switches;

    private readonly UpdateNoticeRow vectorLightRow;
    private readonly UpdateNoticeRow volumetricCloudRow;

    // Set only by the enable button (and by Enter, which is that button's keyboard form). Every
    // other way out of this window leaves it false.
    //
    // IT IS A FLAG AND NOT A READ OF THE UI STATE for a reason worth keeping: the exit path is
    // PostClose, which fires for ways out nobody chose — Escape, another mod closing the stack, the
    // test harness's blocking-dialog sweep. Anything inferred there would take all of those as
    // consent and switch the mod's biggest visual change on for a player who never pressed anything.
    private bool enableVectorLights;

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
        vectorLightRow = UpdateNoticeMath.VectorLightRow(switches);
        volumetricCloudRow = UpdateNoticeMath.VolumetricCloudRow(switches);

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
        DrawVolumetricCloudRow(listing);

        listing.Label("Both switches live in Options → Mod settings → Celestial Lighting, alongside "
            + "the rest of them, if you would rather decide later or change your mind.");

        contentHeight = listing.CurHeight;
        listing.End();

        Widgets.EndScrollView();

        DrawButtons(inRect);
    }

    private void DrawVectorLightRow(Listing_Standard listing)
    {
        DrawFeature(listing, vectorLightRow,
            heading: "Vector light sources",
            body: "Artificial light cast as a shape from each lamp rather than flooded outwards: a beam "
                + "through a doorway, a hard shadow behind a rock, firelight spilling out of a window. "
                + "Pawns throw a shadow away from every lamp lighting them.\n\n"
                + "This is the biggest change to how a colony looks that this mod has made, which is why "
                + "you are being asked rather than simply given it. The trade is that light which only "
                + "reached a room by bending around a corner no longer arrives, so indirectly lit rooms "
                + "are genuinely darker than you are used to.",
            // No footnote: this row is only ever Offer or Hidden, never Announce — see
            // UpdateNoticeMath.VectorLightRow.
            footnote: null);
    }

    private void DrawVolumetricCloudRow(Listing_Standard listing)
    {
        DrawFeature(listing, volumetricCloudRow,
            heading: "Volumetric clouds",
            body: "The drifting clouds are now lit by marching through a real three-dimensional model of "
                + "them instead of tinting a flat picture. Each cloud shadows its own underside, so a low "
                + "sun lights the tops while the bulk beneath stays dark, and the shape of that shading "
                + "changes through the day rather than just its brightness.",
            footnote: "Already running — this one needed no decision from you. It is the one cloud "
                + "setting whose cost is on the graphics card, so it is the first switch to reach for if "
                + "you find yourself short of frames.");
    }

    // One row: heading, description, and — for an announced feature — a greyed footnote saying it is
    // already running. A Hidden feature draws nothing at all, see UpdateNoticeRow.Hidden. The Offer
    // case draws no control of its own: its button is in the bottom row, where a modal's primary
    // action belongs, and `footnote` is null for it.
    private void DrawFeature(Listing_Standard listing, UpdateNoticeRow row, string heading, string body,
        string footnote)
    {
        if (row == UpdateNoticeRow.Hidden)
            return;

        Text.Font = GameFont.Medium;
        listing.Label(heading);
        Text.Font = GameFont.Small;
        listing.Label(body);

        if (row == UpdateNoticeRow.Announce && footnote != null)
        {
            GUI.color = Color.gray;
            listing.Label(footnote);
            GUI.color = Color.white;
        }

        listing.GapLine();
    }

    private void DrawButtons(Rect inRect)
    {
        float y = inRect.height - ButtonHeight;

        // Nothing to offer means every new feature is either already running or unreachable here, so
        // the window is an announcement and gets one button. Drawing a disabled enable button instead
        // would be asking a question with no answer.
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
        // declining is the default state of `enableVectorLights`, so it only has to close.
        if (Widgets.ButtonText(new Rect(0f, y, width, ButtonHeight), "Not now"))
            Close();

        // The button says what it turns on rather than "OK" or "Apply". This is the mod's largest
        // visual change and the player is one click from a colony that is lit differently, so the
        // click should not be ambiguous about which of the two features above it acts on.
        if (Widgets.ButtonText(new Rect(half + ButtonGap, y, width, ButtonHeight), "Turn on vector lighting"))
        {
            enableVectorLights = true;
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

    // Enter is the keyboard form of the confirm button, so it means what that button means — and
    // only when that button is actually on screen. With nothing offerable the window is an OK box,
    // and Enter must not enable a feature the player was never shown a control for.
    public override void OnAcceptKeyPressed()
    {
        if (UpdateNoticeMath.AnyOffer(switches))
            enableVectorLights = true;

        Close();
        Event.current.Use();
    }

    // THE SINGLE EXIT, and every way out of the window funnels through it — both buttons, Escape,
    // Enter, and anything that closes the window stack from underneath us. That is why the
    // acknowledgement is written here rather than in the enable handler: a notice that only records
    // itself on "yes" comes back every boot for everyone who said no, which is the exact failure the
    // "only shows once" requirement names.
    public override void PostClose()
    {
        base.PostClose();
        UpdateNotice.RecordAnswer(enableVectorLights);
    }
}
