using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace CelestialLighting.Tools;

// Offline look-dev for §27's vector lights. Rasterises the SHIPPED mesh — the very vertex and
// triangle arrays VectorLightMath.BuildMesh hands the game — over a hand-authored cell layout, and
// puts it beside an emulation of vanilla's flood so the comparison is the same one the live harness
// will later measure.
//
// WHY THIS EXISTS. §27 is almost entirely a look question, and the only other way to ask it costs a
// three-minute RimWorld boot plus a shared-DLL race. AuroraPreview's header records that this exact
// tool caught two defects no assertion could have (a field tiled 25 times across the map; ribbons
// rendering as wiry filaments), and the same argument applies harder here: a fan wound the wrong way
// or a ring that fails to tile is invisible to every numeric test and obvious on sight.
//
// It is NOT a substitute for the live run. What it does is make the live run a confirmation rather
// than an exploration.
//
//   dotnet run --project Tools/VectorLightPreview [outputDirectory]
public static class Program
{
    // Sampled from a real harness night capture (aurora_curtain_off.png), by way of AuroraPreview.
    // Judging an additive pass against black flatters it: everything looks luminous next to nothing.
    private static readonly float[] NightGround = { 0.115f, 0.098f, 0.090f };

    // A torch's warm cast. Vanilla's CompProperties_Glower.glowColor carries components above 255 —
    // it is a ColorInt scaled by 1.45 — so the headroom is real and is where the hot core comes from.
    private static readonly float[] TorchColor = { 1.45f, 1.07f, 0.64f };

    private const int PixelsPerCell = 12;

    public static int Main(string[] args)
    {
        string outputDir = args.Length > 0 ? args[0] : "Tools/VectorLightPreview/out";
        Directory.CreateDirectory(outputDir);

        foreach (Scene scene in Scenes())
            RenderScene(scene, outputDir);

        Console.WriteLine($"wrote previews to {Path.GetFullPath(outputDir)}");
        return 0;
    }

    // The three reference behaviours, as the smallest layouts that provoke each one.
    private static IEnumerable<Scene> Scenes()
    {
        yield return DoorScene();
        yield return BlockerScene();
        yield return WindowScene();
    }

    // Reference 1: two rooms sharing a wall with one doorway, torch in the left room. The behaviour
    // under test is the WEDGE — vanilla's flood puts a soft blob through the gap, ours must put a
    // widening beam with straight edges through the door jambs.
    private static Scene DoorScene()
    {
        Scene scene = new Scene("door", 60, 40);
        scene.Rect(8, 8, 30, 30, hollow: true);
        scene.Rect(30, 8, 52, 30, hollow: true);
        scene.Clear(30, 19, 30, 19);
        scene.Apertures.Add((30, 19, 30, 19));

        // The torch sits near the door on purpose. Framing matters more than it looks: the first cut
        // put it 10.5 cells away on a radius of 14, which left under 4 cells of reach beyond the
        // opening and read as "the beam does not work" when the geometry was already correct. A
        // scene that starves the effect of range is indistinguishable from a broken one.
        scene.Lights.Add(new Light(24.5f, 19.5f, 18f));
        return scene;
    }

    // Reference 2: open ground, one lamp, two loose blockers. The behaviour under test is the
    // SHADOW — two hard wedges diverging away from the light, which vanilla cannot express at all
    // because its grid records distance travelled and never direction of travel.
    private static Scene BlockerScene()
    {
        Scene scene = new Scene("blockers", 60, 40);
        scene.Rect(27, 18, 29, 19, hollow: false);
        scene.Rect(32, 18, 34, 19, hollow: false);
        scene.Lights.Add(new Light(30.5f, 26.5f, 16f));
        return scene;
    }

    // Reference 3: a fire indoors and one window in the wall. The behaviour under test is SPILL —
    // light escaping a single gap and landing outside as a cone on the ground.
    private static Scene WindowScene()
    {
        Scene scene = new Scene("window", 60, 40);
        scene.Rect(8, 6, 26, 34, hollow: true);
        scene.Clear(26, 20, 26, 20);
        scene.Apertures.Add((26, 20, 26, 20));
        scene.Lights.Add(new Light(23.5f, 20.5f, 18f));
        return scene;
    }

    private static void RenderScene(Scene scene, string outputDir)
    {
        // The BAKE is the number that matters — it is what the game pays when a light or a wall
        // changes, and §16's ledger is unforgiving about it. Timing it together with the rasterise
        // would bury it under half a million pixels of work the game never does, which is exactly the
        // kind of measurement that reports a subsystem as free.
        double bakeMs = TimeBake(scene);

        // Both arms of the soft-edge A/B come from the same call with a different source radius,
        // which is exactly how the shipped flag works — off is a point source, not a second path.
        float[] hard = AccumulateVector(scene, 0f, out int _, out int hardVerts, out int hardTris);
        float[] soft = AccumulateVector(
            scene, VectorLightMath.DefaultSourceRadius, out int rays, out int verts, out int tris);
        float[] flood = AccumulateFlood(scene);
        float[] spill = AccumulateVector(
            scene, VectorLightMath.DefaultSourceRadius, spill: true, out int _, out int _, out int _);

        Console.WriteLine(
            $"{scene.Name,-10} bake {bakeMs,6:F3} ms  rays {rays,4}  " +
            $"verts {hardVerts,5}->{verts,5}  tris {hardTris,5}->{tris,5}");

        // HOW CLOSE THE WASH GETS TO THE FLOOD, which is the whole question the aperture spill was
        // built to answer and the one a picture alone will not settle. Reported for the beam as well
        // as for the beam plus the wash, so the number reads as a MOVEMENT rather than as a score:
        // "closer than what" is the only useful form of it.
        Console.WriteLine(
            $"{"",-10} vs flood  beam {AgreementReport(scene, flood, soft)}   " +
            $"beam+spill {AgreementReport(scene, flood, spill)}");

        Write(Path.Combine(outputDir, $"{scene.Name}_vector.png"), scene, soft);
        Write(Path.Combine(outputDir, $"{scene.Name}_flood.png"), scene, flood);
        Write(Path.Combine(outputDir, $"{scene.Name}_spill.png"), scene, spill);
        WritePair(Path.Combine(outputDir, $"{scene.Name}_ab.png"), scene, flood, soft);
        WritePair(Path.Combine(outputDir, $"{scene.Name}_soft_ab.png"), scene, hard, soft);
        WritePair(Path.Combine(outputDir, $"{scene.Name}_spill_ab.png"), scene, soft, spill);
        WriteTriple(Path.Combine(outputDir, $"{scene.Name}_abc.png"), scene, flood, hard, soft);
        WriteTriple(Path.Combine(outputDir, $"{scene.Name}_spill_abc.png"), scene, flood, soft, spill);
    }

    // One full bake of every light in the scene: silhouette extraction, visibility polygon, mesh.
    // Averaged over enough repeats that the stopwatch resolution is not the measurement.
    private static double TimeBake(Scene scene)
    {
        const int Repeats = 200;
        Stopwatch watch = Stopwatch.StartNew();

        for (int i = 0; i < Repeats; i++)
        {
            foreach (Light source in scene.Lights)
            {
                VectorLightMath.Segment[] segments = WindowedSegments(scene, source);

                VectorLightMath.LightPolygon polygon = VectorLightMath.Build(
                    source.X, source.Z, source.Radius, segments, VectorLightMath.DefaultBaseRayCount);
                VectorLightMath.BuildMesh(
                    source.X, source.Z, source.Radius, polygon, VectorLightMath.DefaultSourceRadius);
            }
        }

        return watch.Elapsed.TotalMilliseconds / Repeats;
    }

    // ---- the shipped path ------------------------------------------------------------------

    private static float[] AccumulateVector(
        Scene scene, float sourceRadius, out int rays, out int verts, out int tris)
    {
        return AccumulateVector(scene, sourceRadius, spill: false, out rays, out verts, out tris);
    }

    private static float[] AccumulateVector(
        Scene scene, float sourceRadius, bool spill, out int rays, out int verts, out int tris)
    {
        int width = scene.Width * PixelsPerCell;
        int height = scene.Height * PixelsPerCell;
        float[] light = new float[width * height];
        float[] single = new float[width * height];

        rays = 0;
        verts = 0;
        tris = 0;

        foreach (Light source in scene.Lights)
        {
            VectorLightMath.Segment[] segments = WindowedSegments(scene, source);

            VectorLightMath.LightPolygon polygon = VectorLightMath.Build(
                source.X, source.Z, source.Radius, segments, VectorLightMath.DefaultBaseRayCount);

            VectorLightMath.LightMesh mesh =
                VectorLightMath.BuildMesh(source.X, source.Z, source.Radius, polygon, sourceRadius);

            rays += polygon.Count;
            verts += mesh.VertexCount;
            tris += mesh.Triangles.Length / 3;

            // One buffer per light, combined with max, then summed into the scene. Within a single
            // light the triangles TILE — they share edges and agree on the value along them — so max
            // is exact there and immune to whether a pixel centre lands inside one neighbour, both,
            // or neither. Summing is reserved for the genuinely additive case, which is one light on
            // top of another. Doing it the other way round is what put hairlines down every radial
            // seam in the first cut: bright ones with an inclusive edge test, dark ones with a strict
            // one, and neither is anything the game would draw.
            byte[] gradient = VectorLightMath.PenumbraGradient(
                source.Radius, VectorLightMath.GradientSize, VectorLightMath.PenumbraGradientSize);

            Array.Clear(single, 0, single.Length);
            RasterizeMesh(mesh, gradient, single, width, height);

            // INTO THE SAME PER-LIGHT BUFFER, WHICH IS WHAT MAKES THE MAX EXACT. RasterizeTriangle
            // combines with max, and the spill is this light's own light arriving by a longer route —
            // so it belongs inside this light's buffer, beside its own triangles, and not summed on
            // top afterwards. ApertureSpillMath's header has the triangle-inequality argument for why
            // that composition needs no explicit suppression of the overlap.
            if (spill)
            {
                AccumulateSpill(scene, source, sourceRadius, gradient, single, width, height);
            }

            for (int i = 0; i < light.Length; i++)
                light[i] += single[i];
        }

        return light;
    }

    // How much of vanilla's flood a candidate render actually covers, and how much it invents.
    //
    // TWO NUMBERS AND NOT ONE, because a single "difference" cannot tell the two failures apart and
    // they mean opposite things here. `lit` is the share of the flood's own lit area that the
    // candidate also lights — the wash's whole purpose is to raise it. `over` is the share of the
    // candidate's lit area that the flood does NOT light, which is the beam doing what vector
    // lighting exists to do (a straight edge where the flood has a smear) and must NOT be read as an
    // error. A model scoring 1.00 on both would BE the flood, which is not the goal.
    //
    // Thresholded rather than differenced because the question is where light is, not what value it
    // has: the two models disagree about brightness everywhere by construction, and averaging that in
    // would drown the coverage answer this is asked for.
    private static string AgreementReport(Scene scene, float[] flood, float[] candidate)
    {
        const float Threshold = 0.02f;

        int floodLit = 0;
        int candidateLit = 0;
        int both = 0;

        for (int i = 0; i < flood.Length; i++)
        {
            bool inFlood = flood[i] > Threshold;
            bool inCandidate = candidate[i] > Threshold;

            if (inFlood)
            {
                floodLit++;
            }

            if (inCandidate)
            {
                candidateLit++;
            }

            if (inFlood && inCandidate)
            {
                both++;
            }
        }

        float covered = floodLit > 0 ? (float)both / floodLit : 0f;
        float over = candidateLit > 0 ? (float)(candidateLit - both) / candidateLit : 0f;
        return $"covers {covered,5:P1} of flood, {over,5:P1} outside it";
    }

    // One secondary emitter per opening this light can still reach: the doorway wash, built out of
    // exactly the same three calls the lamp itself uses.
    //
    // THE ONLY THING THAT IS NEW IS THE TEXTURE COORDINATE. BuildMesh writes U = distance/reach,
    // i.e. a curve that restarts at the opening; ApertureSpillMath.SpillU says what it should be —
    // the lamp's own curve entered at the distance the light has already travelled. Remapping the
    // array afterwards rather than teaching BuildMesh a second convention keeps the shipped mesh
    // builder with one meaning for U, which matters because the game's fragment program reads it.
    private static void AccumulateSpill(
        Scene scene, Light source, float sourceRadius, byte[] gradient,
        float[] single, int width, int height)
    {
        foreach ((int minX, int minZ, int maxX, int maxZ) in scene.Apertures)
        {
            float apertureX = ApertureSpillMath.ApertureCentre(minX, maxX);
            float apertureZ = ApertureSpillMath.ApertureCentre(minZ, maxZ);

            ApertureSpillMath.SpillOrigin(
                source.X, source.Z, apertureX, apertureZ, ApertureSpillMath.DefaultSpillPush,
                out float originX, out float originZ);

            float dx = originX - source.X;
            float dz = originZ - source.Z;
            float d0 = (float)Math.Sqrt(dx * dx + dz * dz);

            // EUCLIDEAN AND NOT GEODESIC, and the difference is exactly the model's claim. Inside the
            // room the lamp is standing in there is nothing in the way, so the two agree; where they
            // would not, the lamp cannot see the doorway at all and the wash it throws is not this
            // light's to throw. A geodesic here would put light through a door the lamp only reaches
            // around a corner, which is the flood's behaviour and the thing being replaced.
            if (!ApertureSpillMath.Spills(source.Radius, d0))
            {
                continue;
            }

            float reach = ApertureSpillMath.ResidualReach(source.Radius, d0);
            Light opening = new Light(originX, originZ, reach);
            VectorLightMath.Segment[] segments = WindowedSegments(scene, opening);

            VectorLightMath.LightPolygon polygon = VectorLightMath.Build(
                originX, originZ, reach, segments, VectorLightMath.DefaultBaseRayCount);

            VectorLightMath.LightMesh mesh =
                VectorLightMath.BuildMesh(originX, originZ, reach, polygon, sourceRadius);

            for (int i = 0; i < mesh.U.Length; i++)
            {
                mesh.U[i] = ApertureSpillMath.SpillU(source.Radius, d0, mesh.U[i] * reach);
            }

            RasterizeMesh(mesh, gradient, single, width, height);
        }
    }

    // Scanline-free barycentric fill. Deliberately ignores winding — the preview must show the
    // triangles whichever way round they are, or a winding bug would present here as "the maths is
    // broken" instead of as the culling problem it actually is. Winding is the live run's job.
    private static void RasterizeMesh(
        VectorLightMath.LightMesh mesh, byte[] gradient, float[] light, int width, int height)
    {
        for (int t = 0; t < mesh.Triangles.Length; t += 3)
        {
            int a = mesh.Triangles[t];
            int b = mesh.Triangles[t + 1];
            int c = mesh.Triangles[t + 2];
            RasterizeTriangle(mesh, gradient, a, b, c, light, width, height);
        }
    }

    private static void RasterizeTriangle(
        VectorLightMath.LightMesh mesh, byte[] gradient, int a, int b, int c,
        float[] light, int width, int height)
    {
        float ax = mesh.X[a] * PixelsPerCell;
        float az = mesh.Z[a] * PixelsPerCell;
        float bx = mesh.X[b] * PixelsPerCell;
        float bz = mesh.Z[b] * PixelsPerCell;
        float cx = mesh.X[c] * PixelsPerCell;
        float cz = mesh.Z[c] * PixelsPerCell;

        float area = (bx - ax) * (cz - az) - (cx - ax) * (bz - az);

        if (Math.Abs(area) < 1e-6f)
            return;

        int minX = Math.Max((int)Math.Floor(Math.Min(ax, Math.Min(bx, cx))), 0);
        int maxX = Math.Min((int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))), width - 1);
        int minZ = Math.Max((int)Math.Floor(Math.Min(az, Math.Min(bz, cz))), 0);
        int maxZ = Math.Min((int)Math.Ceiling(Math.Max(az, Math.Max(bz, cz))), height - 1);

        for (int py = minZ; py <= maxZ; py++)
        {
            for (int px = minX; px <= maxX; px++)
            {
                float sx = px + 0.5f;
                float sz = py + 0.5f;

                float w0 = ((bx - sx) * (cz - sz) - (cx - sx) * (bz - sz)) / area;
                float w1 = ((cx - sx) * (az - sz) - (ax - sx) * (cz - sz)) / area;
                float w2 = 1f - w0 - w1;

                // Inclusive on all three edges, which is safe only because the caller combines
                // triangles with max rather than by adding them — see AccumulateVector.
                bool inside = w0 >= 0f && w1 >= 0f && w2 >= 0f;

                if (inside)
                {
                    // Interpolate the texture coordinate and then look the gradient up, which is the
                    // order the GPU does it in. Interpolating brightness instead would silently
                    // linearise the falloff curve and make the preview flatter than the game.
                    float u = w0 * mesh.U[a] + w1 * mesh.U[b] + w2 * mesh.U[c];
                    float v = w0 * mesh.V[a] + w1 * mesh.V[b] + w2 * mesh.V[c];
                    float value = Sample(gradient, u, v);
                    int index = py * width + px;
                    light[index] = Math.Max(light[index], value);
                }
            }
        }
    }

    // Exactly the extraction the game adapter has to do: the blockers within one light's reach,
    // padded by a cell so a wall just outside the radius still occludes, clipped to the map. Modelled
    // here rather than handing the whole scene over, so the preview measures the bake the game will
    // actually pay and exercises the world-coordinate offset rather than assuming it.
    private static VectorLightMath.Segment[] WindowedSegments(Scene scene, Light source)
    {
        int pad = (int)Math.Ceiling(source.Radius) + 1;
        int minX = Math.Max((int)source.X - pad, 0);
        int minZ = Math.Max((int)source.Z - pad, 0);
        int maxX = Math.Min((int)source.X + pad, scene.Width - 1);
        int maxZ = Math.Min((int)source.Z + pad, scene.Height - 1);

        int w = maxX - minX + 1;
        int h = maxZ - minZ + 1;
        bool[] blocked = new bool[w * h];

        for (int z = 0; z < h; z++)
        {
            for (int x = 0; x < w; x++)
                blocked[z * w + x] = scene.BlockedAt(minX + x, minZ + z);
        }

        return VectorLightMath.SilhouetteSegments(blocked, w, h, minX, minZ);
    }

    // Nearest-neighbour on both axes, matching what the 1-D version did. The GPU filters
    // bilinearly and so comes out marginally smoother than this; erring the other way would let the
    // preview flatter a banding artefact the game would actually show.
    private static float Sample(byte[] gradient, float u, float v)
    {
        int columns = VectorLightMath.GradientSize;
        int rows = VectorLightMath.PenumbraGradientSize;

        int column = (int)Math.Round(Math.Clamp(u, 0f, 1f) * (columns - 1));
        int row = (int)Math.Round(Math.Clamp(v, 0f, 1f) * (rows - 1));

        return gradient[row * columns + column] / 255f;
    }

    // ---- vanilla's flood, for the A/B ------------------------------------------------------

    // A faithful-enough emulation of Verse.Glow.ComputeGlowGridsJob: Dijkstra on the 8-neighbour
    // lattice with 100/141 fixed-point costs, a diagonal refused only when BOTH flanking cardinals
    // are blockers, and the same falloff curve evaluated on the GEODESIC distance. It is here purely
    // so the "before" in these previews is the real before rather than a remembered one.
    private static float[] AccumulateFlood(Scene scene)
    {
        int width = scene.Width * PixelsPerCell;
        int height = scene.Height * PixelsPerCell;
        float[] light = new float[width * height];

        foreach (Light source in scene.Lights)
        {
            float[] cells = FloodCells(scene, source);
            SplatCells(cells, scene, light, width, height);
        }

        return light;
    }

    private static float[] FloodCells(Scene scene, Light source)
    {
        int n = scene.Width * scene.Height;
        float[] dist = new float[n];
        float[] value = new float[n];

        for (int i = 0; i < n; i++)
            dist[i] = float.MaxValue;

        int startX = (int)source.X;
        int startZ = (int)source.Z;
        int start = startZ * scene.Width + startX;

        dist[start] = 1f;
        SortedSet<(float, int)> queue = new SortedSet<(float, int)> { (1f, start) };

        int[] dx = { 0, 1, 0, -1, 1, 1, -1, -1 };
        int[] dz = { -1, 0, 1, 0, -1, 1, 1, -1 };

        while (queue.Count > 0)
        {
            (float d, int index) = queue.Min;
            queue.Remove(queue.Min);
            value[index] = VectorLightMath.Falloff(d, source.Radius);
            RelaxNeighbours(scene, source, dist, queue, dx, dz, index, d);
        }

        return value;
    }

    private static void RelaxNeighbours(
        Scene scene, Light source, float[] dist, SortedSet<(float, int)> queue,
        int[] dx, int[] dz, int index, float d)
    {
        int x = index % scene.Width;
        int z = index / scene.Width;

        for (int k = 0; k < 8; k++)
        {
            int nx = x + dx[k];
            int nz = z + dz[k];

            if (!Passable(scene, x, z, nx, nz, k))
                continue;

            float step = k < 4 ? 1f : 1.41f;
            float nd = d + step;
            int ni = nz * scene.Width + nx;

            if (nd < dist[ni] && nd <= source.Radius)
            {
                dist[ni] = nd;
                queue.Add((nd, ni));
            }
        }
    }

    private static bool Passable(Scene scene, int x, int z, int nx, int nz, int k)
    {
        bool inside = nx >= 0 && nx < scene.Width && nz >= 0 && nz < scene.Height;

        if (!inside || scene.Blocked[nz * scene.Width + nx])
            return false;

        if (k < 4)
            return true;

        // Vanilla refuses a diagonal only when both flanking cardinals are blockers, which is
        // precisely why a single diagonal gap leaks light in the base game.
        bool flankA = scene.BlockedAt(nx, z);
        bool flankB = scene.BlockedAt(x, nz);
        return !(flankA && flankB);
    }

    private static void SplatCells(float[] cells, Scene scene, float[] light, int width, int height)
    {
        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                int cx = px / PixelsPerCell;
                int cz = py / PixelsPerCell;
                light[py * width + px] += cells[cz * scene.Width + cx];
            }
        }
    }

    // ---- output ----------------------------------------------------------------------------

    private static void Write(string path, Scene scene, float[] light)
    {
        int width = scene.Width * PixelsPerCell;
        int height = scene.Height * PixelsPerCell;
        Png.Write(path, width, height, Compose(scene, light, width, height));
    }

    private static void WritePair(string path, Scene scene, float[] left, float[] right)
    {
        int width = scene.Width * PixelsPerCell;
        int height = scene.Height * PixelsPerCell;
        const int Gap = 8;

        byte[] a = Compose(scene, left, width, height);
        byte[] b = Compose(scene, right, width, height);
        byte[] outPixels = new byte[(width * 2 + Gap) * height * 4];

        for (int y = 0; y < height; y++)
        {
            Array.Copy(a, y * width * 4, outPixels, (y * (width * 2 + Gap)) * 4, width * 4);
            Array.Copy(b, y * width * 4, outPixels, (y * (width * 2 + Gap) + width + Gap) * 4, width * 4);
        }

        Png.Write(path, width * 2 + Gap, height, outPixels);
    }

    // Three arms in one strip, in the order they happened: vanilla's flood, then phase 1's
    // visibility polygon, then phase 2's penumbra. Worth its own writer rather than two pairs,
    // because the interesting comparison is not either step on its own — it is that the first step
    // changes the SHAPE of the light and the second only softens where that shape ends.
    private static void WriteTriple(
        string path, Scene scene, float[] left, float[] middle, float[] right)
    {
        int width = scene.Width * PixelsPerCell;
        int height = scene.Height * PixelsPerCell;
        const int Gap = 8;

        byte[][] panels = { Compose(scene, left, width, height), Compose(scene, middle, width, height),
                            Compose(scene, right, width, height) };
        int stride = width * 3 + Gap * 2;
        byte[] outPixels = new byte[stride * height * 4];

        for (int panel = 0; panel < panels.Length; panel++)
        {
            int offset = panel * (width + Gap);

            for (int y = 0; y < height; y++)
                Array.Copy(panels[panel], y * width * 4, outPixels, (y * stride + offset) * 4, width * 4);
        }

        Png.Write(path, stride, height, outPixels);
    }

    private static byte[] Compose(Scene scene, float[] light, int width, int height)
    {
        byte[] rgba = new byte[width * height * 4];

        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                // +z is up on screen but PNG rows run downward. Marked because getting this wrong
                // mirrors every shadow vertically and still looks plausible.
                int flipped = height - 1 - py;
                int cx = px / PixelsPerCell;
                int cz = flipped / PixelsPerCell;

                float value = light[flipped * width + px];
                bool wall = scene.BlockedAt(cx, cz);
                WritePixel(rgba, (py * width + px) * 4, value, wall);
            }
        }

        return rgba;
    }

    private static void WritePixel(byte[] rgba, int offset, float value, bool wall)
    {
        // Walls are drawn as a flat slab rather than lit, so the eye reads the shadow boundary
        // against the obstruction that caused it instead of against a gradient on the wall itself.
        float ambient = wall ? 0.055f : 1f;

        for (int c = 0; c < 3; c++)
        {
            float lit = wall ? 0f : value * TorchColor[c];
            rgba[offset + c] = ToByte(NightGround[c] * ambient + lit);
        }

        rgba[offset + 3] = 255;
    }

    private static byte ToByte(float value)
    {
        int scaled = (int)Math.Round(Math.Clamp(value, 0f, 1f) * 255f);
        return (byte)scaled;
    }

    // ---- scene authoring -------------------------------------------------------------------

    private sealed class Light
    {
        public readonly float X;
        public readonly float Z;
        public readonly float Radius;

        public Light(float x, float z, float radius)
        {
            X = x;
            Z = z;
            Radius = radius;
        }
    }

    private sealed class Scene
    {
        public readonly string Name;
        public readonly int Width;
        public readonly int Height;
        public readonly bool[] Blocked;
        public readonly List<Light> Lights = new List<Light>();

        // The openings a doorway spill radiates from, as (minX, minZ, maxX, maxZ) cell spans. Held on
        // the scene rather than derived from the blocker grid because "which gap is a door" is a
        // question the game answers from thing defs and this preview has no things — deriving it here
        // would be a second, different rule that could agree with the game on these three layouts and
        // disagree everywhere else.
        public readonly List<(int MinX, int MinZ, int MaxX, int MaxZ)> Apertures =
            new List<(int, int, int, int)>();

        public Scene(string name, int width, int height)
        {
            Name = name;
            Width = width;
            Height = height;
            Blocked = new bool[width * height];
        }

        public bool BlockedAt(int x, int z)
        {
            bool inside = x >= 0 && x < Width && z >= 0 && z < Height;
            return inside && Blocked[z * Width + x];
        }

        public void Rect(int x0, int z0, int x1, int z1, bool hollow)
        {
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    bool edge = x == x0 || x == x1 || z == z0 || z == z1;

                    if (!hollow || edge)
                        Blocked[z * Width + x] = true;
                }
            }
        }

        public void Clear(int x0, int z0, int x1, int z1)
        {
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                    Blocked[z * Width + x] = false;
            }
        }
    }
}
