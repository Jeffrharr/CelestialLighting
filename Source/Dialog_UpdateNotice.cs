using UnityEngine;
using Verse;

namespace CelestialLighting;

// The one-time notice: offers vector lighting to a player who already had this mod, and tells them
// that volumetric clouds arrived with it. The thin Verse adapter over UpdateNoticeMath — every
// decision about whether to appear and what to say about each feature lives there; this file draws
// it and writes the answer back. See DESIGN.md "Update notice".
//
// ONLY AN UPGRADING INSTALL EVER SEES THIS WINDOW. A first-time install gets vector lighting switched
// on and no window at all, because nothing in this mod is an "update" to somebody for whom every
// effect arrived at the same moment — see UpdateNoticeMath.ShouldShow and FirstRunSwitches. So the
// prose here can address a returning player directly, and does.
//
// WHY THEY ARE ASKED RATHER THAN SIMPLY GIVEN IT, now that vector lighting is no longer the mod's
// expensive outlier: it changes how a colony is LIT. Light that vanilla delivered along a path
// bending around a corner no longer arrives, so indirectly lit rooms are genuinely darker. Somebody
// fifty hours into a colony lit the other way has a specific expectation, and silently rewriting it
// on update is not a default, it is a surprise.
//
// The other half is discovery. Vector lighting staying off for this population means the only route
// to it is reading a list of thirty checkboxes and noticing one; volumetric clouds arrive already
// running and change what the sky looks like without ever being mentioned, so a player whose clouds
// suddenly render differently has no way to connect that to a setting.
//
// ONE BUTTON, NOT A TICKBOX PER FEATURE. Only one of the two is offerable at all — the cloud row is
// announcement-only by design (UpdateNoticeMath.VolumetricCloudRow) — so a checkbox plus an Apply
// button would be two clicks and a moment's reading to express one yes. It is also the more honest
// shape for the thing being asked: a button that says what it does is a clearer commitment than a
// pre-ticked box the player might not register having agreed to.
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
    // consent and switch the mod's most expensive feature on for a player who never pressed
    // anything.
    private bool enableVectorLights;

    private Vector2 scrollPosition;
    private float contentHeight;

    private const float TitleHeight = 42f;
    private const float ButtonHeight = 35f;
    private const float ButtonGap = 10f;
    private const float ScrollBarWidth = 20f;

    // Height follows the content, because either feature block can be absent and a fixed size is
    // visibly wrong for whichever case it was not picked for. BOTH ERRORS THAT PRODUCED THESE
    // NUMBERS WERE FOUND BY LOOKING AT THE THING — this shipped at a flat 520, where the two-block
    // case pushed its closing line below the fold behind a scrollbar (so the one sentence telling
    // the player they can change their mind was the one they could not see), and a flat 620 left the
    // one-block case with 200px of empty black under three paragraphs.
    //
    // ChromeHeight is the title, the intro, the closing line and the button row — everything that is
    // there whatever the rows say. The two row constants are added for the blocks actually drawn, so
    // a player who already switched vector lighting on gets a window sized for the cloud
    // announcement alone rather than one sized for a block it does not contain.
    //
    // The scroll view stays regardless: a UI-scale or font setting can push any content past any
    // fixed height, and a scrollbar is the right answer then. Nothing should need it at defaults.
    //
    // Capped where it is because RimWorld supports 1024x768 — a centred 620 still leaves ~74px of
    // margin above and below there.
    private const float ChromeHeight = 260f;
    private const float VectorLightRowHeight = 200f;
    private const float CloudRowHeight = 160f;

    public override Vector2 InitialSize => new Vector2(
        640f,
        ChromeHeight
        + (vectorLightRow == UpdateNoticeRow.Hidden ? 0f : VectorLightRowHeight)
        + (volumetricCloudRow == UpdateNoticeRow.Hidden ? 0f : CloudRowHeight));

    // Raises the notice for whatever the settings say right now. Used by the test harness's
    // RaiseWindow step, which constructs by type name and therefore needs a parameterless
    // constructor — the notice's own draw path cannot be photographed any other way, because a
    // window raised at the main menu is discarded when the game loads (DESIGN.md "What was not
    // verified"). Also the constructor any future "show me what's new again" button would want.
    public Dialog_UpdateNotice()
        : this(UpdateNotice.CurrentSwitches())
    {
    }

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

    // "What's new" is a claim about the player's history, and every player who reaches this window
    // has one — UpdateNoticeMath.ShouldShow raises it only for an install that had the mod before.
    private const string Title = "Celestial Lighting — what's new";

    // COUNTS THE BLOCKS ACTUALLY DRAWN rather than hard-coding "two". Either row can be absent — the
    // vector one because the player already found the switch, the cloud one because the renderer is
    // not reachable on this machine — and a window that opens by announcing two effects above a
    // single block is the mod miscounting its own release notes in front of the player.
    private string Intro
    {
        get
        {
            int blocks = (vectorLightRow == UpdateNoticeRow.Hidden ? 0 : 1)
                + (volumetricCloudRow == UpdateNoticeRow.Hidden ? 0 : 1);

            return (blocks > 1
                    ? "This update adds two new effects. Both are visual only — "
                    : "This update adds a new effect. It is visual only — ")
                + "nothing here changes plant growth, work speed, pawn vision or anything else the "
                + "game reads a number from.";
        }
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, inRect.y, inRect.width, TitleHeight), Title);
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

        listing.Label(Intro);
        listing.Gap();

        DrawVectorLightRow(listing);
        DrawVolumetricCloudRow(listing);

        listing.Label("You can change your mind at any time in Options → Mod settings → Celestial "
            + "Lighting, where the rest of the switches live too.");

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
                + "You are being asked rather than simply given it because it changes how your colony "
                + "is lit, not because it is costly: light which only reached a room by bending around "
                + "a corner no longer arrives, so indirectly lit rooms are genuinely darker than you "
                + "are used to. Nothing else changes — plant growth, work speed, pawn vision and mood "
                + "read exactly the same numbers either way. A fresh install of this mod starts with "
                + "it on; your colony keeps the lighting it has until you say otherwise.",
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
            footnote: "Already running — this one needed no decision from you. Its cost is on the "
                + "graphics card rather than the CPU, and it switches itself off on hardware that "
                + "cannot run it, so nothing here depends on you checking anything.");
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

        // Nothing to offer means the one offerable feature is already on, so the window is an
        // announcement and gets one button. Drawing a disabled enable button instead would be asking
        // a question with no answer.
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

        // The button says what it turns on rather than "OK" or "Apply". The player is one click from
        // a colony that is both lit differently and measurably more expensive to draw, so the click
        // should not be ambiguous about which of the features above it acts on.
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
