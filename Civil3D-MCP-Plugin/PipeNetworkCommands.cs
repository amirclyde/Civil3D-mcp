using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using CivilEntity = Autodesk.Civil.DatabaseServices.Entity;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// Gravity pipe networks (civil3d_pipe), phase P0 — written against the typed Civil 3D 2026 API only.
///
/// Elevation conventions used throughout (verified against the AeccDbMgd 2026 member list):
///   Pipe.StartPoint / EndPoint are the 3D centreline end points.
///   invert = centreline Z − InnerHeight / 2, crown = centreline Z + InnerHeight / 2,
///   outer top = centreline Z + OuterHeight / 2 (cover is measured from the surface to the outer top).
///   slopePercent is computed from the inverts over the 2D end-to-end distance; positive = falls from start to end.
///   The raw API value Pipe.Slope ("absolute value", units undocumented) is returned as apiSlope for comparison.
/// </summary>
public static class PipeNetworkCommands
{
  private const int Digits = 4;

  // =============================================================================================
  // Read
  // =============================================================================================

  public static Task<object?> ListPipeNetworksAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var networks = EnumeratePipeNetworks(civilDoc, transaction, OpenMode.ForRead)
        .Select(network => ToNetworkSummary(network, transaction))
        .ToList();
      return new Dictionary<string, object?> { ["networks"] = networks, ["count"] = networks.Count };
    });
  }

  public static Task<object?> GetPipeNetworkAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetOptionalString(parameters, "name") ?? PluginRuntime.GetRequiredString(parameters, "networkName");
    var includeParts = PluginRuntime.GetOptionalBool(parameters, "includeParts") ?? true;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, name, OpenMode.ForRead);
      return ToNetworkDetail(network, transaction, includeParts);
    });
  }

  public static Task<object?> GetPipeAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var pipeKey = PluginRuntime.GetOptionalString(parameters, "pipeName") ?? PluginRuntime.GetRequiredString(parameters, "handle");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var pipe = FindPipe(network, transaction, pipeKey, OpenMode.ForRead);
      return ToPipeData(pipe, transaction);
    });
  }

  public static Task<object?> GetStructureAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var structureKey = PluginRuntime.GetOptionalString(parameters, "structureName") ?? PluginRuntime.GetRequiredString(parameters, "handle");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var structure = FindStructure(network, transaction, structureKey, OpenMode.ForRead);
      return ToStructureData(structure, transaction);
    });
  }

  /// <summary>Parts lists → pipe and structure families → sizes (typed PartsList / PartFamily / PartSize).</summary>
  public static Task<object?> ListPipePartsCatalogAsync(JsonObject? parameters)
  {
    var partsListName = PluginRuntime.GetOptionalString(parameters, "partsList");
    var includeFields = PluginRuntime.GetOptionalBool(parameters, "includeFields") ?? false;
    var includeCatalog = PluginRuntime.GetOptionalBool(parameters, "includeCatalog") ?? false;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var lengthFactor = CatalogToDrawingFactor(database);
      var result = new List<Dictionary<string, object?>>();
      var names = new List<string>();
      foreach (var partsList in EnumeratePartsLists(civilDoc, transaction))
      {
        var listName = CivilObjectUtils.GetName(partsList) ?? "";
        names.Add(listName);
        if (!string.IsNullOrWhiteSpace(partsListName) && !string.Equals(listName, partsListName, StringComparison.OrdinalIgnoreCase)) continue;

        var pipeFamilies = DescribeFamilies(partsList, DomainType.Pipe, transaction, includeFields, lengthFactor);
        var structureFamilies = DescribeFamilies(partsList, DomainType.Structure, transaction, includeFields, lengthFactor);
        result.Add(new Dictionary<string, object?>
        {
          ["name"] = listName,
          ["handle"] = CivilObjectUtils.GetHandle(partsList),
          ["pipeFamilies"] = pipeFamilies,
          ["structureFamilies"] = structureFamilies,
          // Flat list of size names, kept for the legacy size_network workflow.
          ["parts"] = pipeFamilies.Concat(structureFamilies)
            .SelectMany(f => (List<Dictionary<string, object?>>)f["sizes"]!)
            .Select(s => s["name"] as string)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList(),
        });
      }

      if (!string.IsNullOrWhiteSpace(partsListName) && result.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Parts list '{partsListName}' was not found. Available: {string.Join(", ", names)}.");

      var response = new Dictionary<string, object?> { ["partsLists"] = result };
      if (includeCatalog)
      {
        response["catalog"] = new Dictionary<string, object?>
        {
          ["pipeFamilies"] = DescribeCatalogFamilies(DomainType.Pipe),
          ["structureFamilies"] = DescribeCatalogFamilies(DomainType.Structure),
          ["note"] = "Families available in the drawing's pipe network catalog (Set Pipe Network Catalog). Families not in a parts list can be added to one; new families or shapes need Parts Builder or catalog edits.",
        };
      }
      return response;
    });
  }

  /// <summary>Shortest connected path between two parts (Network.FindShortestNetworkPath).</summary>
  public static Task<object?> GetPipeNetworkPathAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var fromKey = PluginRuntime.GetRequiredString(parameters, "fromPart");
    var toKey = PluginRuntime.GetRequiredString(parameters, "toPart");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var from = FindPart(network, transaction, fromKey, OpenMode.ForRead);
      var to = FindPart(network, transaction, toKey, OpenMode.ForRead);
      double minLength = 0;
      ObjectIdCollection ids;
      try { ids = Network.FindShortestNetworkPath(from.ObjectId, to.ObjectId, ref minLength); }
      catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"No path was found between '{fromKey}' and '{toKey}': {ex.Message}"); }

      var parts = ids.Cast<ObjectId>().Select(id =>
      {
        var part = CivilObjectUtils.GetRequiredObject<Part>(transaction, id, OpenMode.ForRead);
        return new Dictionary<string, object?>
        {
          ["name"] = part.Name,
          ["handle"] = CivilObjectUtils.GetHandle(part),
          ["kind"] = part is Pipe ? "pipe" : "structure",
        };
      }).ToList();
      return new Dictionary<string, object?> { ["networkName"] = network.Name, ["length"] = R(minLength), ["parts"] = parts };
    });
  }

  // =============================================================================================
  // Network create / edit / delete
  // =============================================================================================

  public static Task<object?> CreatePipeNetworkAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var partsListName = PluginRuntime.GetRequiredString(parameters, "partsList");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      if (EnumeratePipeNetworks(civilDoc, transaction, OpenMode.ForRead).Any(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Pipe network '{name}' already exists.");

      // Resolve everything before creating, so a bad name leaves no half-made network behind.
      var partsListId = FindPartsListId(civilDoc, transaction, partsListName);
      var requested = name;
      var networkId = Network.Create((CivilDocument)civilDoc, ref requested);
      var network = CivilObjectUtils.GetRequiredObject<Network>(transaction, networkId, OpenMode.ForWrite);
      network.PartsListId = partsListId;
      var warnings = new List<string>();
      if (!string.Equals(requested, name, StringComparison.Ordinal))
        warnings.Add($"Civil 3D named the network '{requested}'.");

      ApplyNetworkSettings(network, parameters, civilDoc, database, transaction, warnings);

      return new Dictionary<string, object?>
      {
        ["created"] = true,
        ["network"] = ToNetworkDetail(network, transaction, includeParts: false),
        ["warnings"] = warnings,
      };
    });
  }

  public static Task<object?> EditPipeNetworkAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, name, OpenMode.ForWrite);
      var warnings = new List<string>();

      var newName = PluginRuntime.GetOptionalString(parameters, "newName");
      if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(newName, network.Name, StringComparison.Ordinal))
      {
        if (EnumeratePipeNetworks(civilDoc, transaction, OpenMode.ForRead).Any(n => n.ObjectId != network.ObjectId && string.Equals(n.Name, newName, StringComparison.OrdinalIgnoreCase)))
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Pipe network '{newName}' already exists.");
        ((CivilEntity)network).Name = newName!;
      }

      var partsListName = PluginRuntime.GetOptionalString(parameters, "partsList");
      if (!string.IsNullOrWhiteSpace(partsListName))
        network.PartsListId = FindPartsListId(civilDoc, transaction, partsListName!);

      ApplyNetworkSettings(network, parameters, civilDoc, database, transaction, warnings);

      return new Dictionary<string, object?>
      {
        ["edited"] = true,
        ["network"] = ToNetworkDetail(network, transaction, includeParts: false),
        ["warnings"] = warnings,
      };
    });
  }

  public static Task<object?> DeletePipeNetworkAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var deleteParts = PluginRuntime.GetOptionalBool(parameters, "deleteParts") ?? false;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, name, OpenMode.ForWrite);
      var pipeIds = network.GetPipeIds().Cast<ObjectId>().ToList();
      var structureIds = network.GetStructureIds().Cast<ObjectId>().ToList();
      if ((pipeIds.Count > 0 || structureIds.Count > 0) && !deleteParts)
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
          $"Pipe network '{name}' still has {pipeIds.Count} pipe(s) and {structureIds.Count} structure(s). Pass deleteParts: true to erase them with the network.");

      foreach (var id in pipeIds.Concat(structureIds))
        transaction.GetObject(id, OpenMode.ForWrite).Erase();
      network.Erase();
      return new Dictionary<string, object?>
      {
        ["deleted"] = true,
        ["name"] = name,
        ["pipesErased"] = pipeIds.Count,
        ["structuresErased"] = structureIds.Count,
      };
    });
  }

  // =============================================================================================
  // Placement
  // =============================================================================================

  /// <summary>
  /// Adds a structure. Rim: rimElevation, or from a surface (surface / the network's reference surface) with
  /// rimAdjustment. Sump: sumpDepth (below the lowest connected invert), sumpElevation, or rimToSumpHeight.
  /// Nothing is assumed: without a rim elevation or a surface the call is refused.
  /// </summary>
  public static Task<object?> AddStructureToNetworkAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var warnings = new List<string>();
      var id = AddStructureCore(network, parameters, x, y, civilDoc, database, transaction, warnings);
      var structure = CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead);
      return new Dictionary<string, object?>
      {
        ["added"] = true,
        ["networkName"] = network.Name,
        ["structure"] = ToStructureData(structure, transaction),
        ["warnings"] = warnings,
      };
    });
  }

  /// <summary>
  /// Adds a straight pipe between two structures and/or points and sets its elevations from, in order of precedence:
  ///   startInvert + endInvert · one invert + slope (%) · startCover / endCover from a surface ·
  ///   legacy centreline Z on startPoint / endPoint · applyRules: true.
  /// Elevations are applied after the part's inner/outer heights are known, then re-checked after connecting.
  /// </summary>
  public static Task<object?> AddPipeToNetworkAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var warnings = new List<string>();
      var id = AddPipeCore(network, parameters, civilDoc, database, transaction, warnings);
      var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead);
      return new Dictionary<string, object?>
      {
        ["added"] = true,
        ["networkName"] = network.Name,
        ["pipe"] = ToPipeData(pipe, transaction),
        ["warnings"] = warnings,
      };
    });
  }

  public static Task<object?> ResizePipeInNetworkAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var pipeName = PluginRuntime.GetRequiredString(parameters, "pipeName");
    var newPartName = PluginRuntime.GetOptionalString(parameters, "newPartName");
    var newDiameter = PluginRuntime.GetOptionalDouble(parameters, "newDiameter");

    if (string.IsNullOrWhiteSpace(newPartName) && !newDiameter.HasValue)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Either 'newPartName' or 'newDiameter' is required.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var pipe = FindPipe(network, transaction, pipeName, OpenMode.ForWrite);

      if (!string.IsNullOrWhiteSpace(newPartName))
      {
        var part = ResolvePartSize(network, transaction, JsonValue.Create(newPartName), DomainType.Pipe, database);
        pipe.SwapPartFamilyAndSize(part.FamilyId, part.SizeId);
      }
      else if (newDiameter.HasValue)
      {
        pipe.ResizeByInnerDiameterOrWidth(newDiameter.Value, useClosestSize: false);
      }

      return new Dictionary<string, object?>
      {
        ["resized"] = true,
        ["pipe"] = ToPipeData(pipe, transaction),
      };
    });
  }

  /// <summary>The upstream stub always reported zero clashes. Refuse until the real check (phase P4) exists.</summary>
  public static Task<object?> CheckPipeNetworkInterferenceAsync(JsonObject? parameters)
  {
    throw new JsonRpcDispatchException("CIVIL3D.NOT_IMPLEMENTED",
      "Interference checking is not implemented yet (planned: solid + clearance check in phase P4). The previous version returned 'no conflicts' without checking anything, so it has been disabled.");
  }

  // =============================================================================================
  // Used by other commands
  // =============================================================================================

  public static int CountPipeNetworks(object civilDoc) => ((CivilDocument)civilDoc).GetPipeNetworkIds().Count;

  public static string? GetFirstPipeNetworkStyleName(object civilDoc, Transaction transaction)
  {
    // Networks have no style; report the first pipe style (what "pipe network style" meant upstream).
    return LookupUtils.GetFirstStyleName(((CivilDocument)civilDoc).Styles.PipeStyles, transaction);
  }

  // =============================================================================================
  // Core placement
  // =============================================================================================

  internal static ObjectId AddStructureCore(Network network, JsonObject? p, double x, double y, CivilDocument civilDoc, Database database, Transaction transaction, List<string> warnings)
  {
    var part = ResolvePartSize(network, transaction, PartNode(p), DomainType.Structure, database);
    var rotationDeg = PluginRuntime.GetOptionalDouble(p, "rotation") ?? 0.0;
    var rimElevation = PluginRuntime.GetOptionalDouble(p, "rimElevation");
    var surfaceName = PluginRuntime.GetOptionalString(p, "surface");
    var rimFromSurface = PluginRuntime.GetOptionalBool(p, "rimFromSurface");
    var rimAdjustment = PluginRuntime.GetOptionalDouble(p, "rimAdjustment") ?? 0.0;
    var sumpDepth = PluginRuntime.GetOptionalDouble(p, "sumpDepth");
    var sumpElevation = PluginRuntime.GetOptionalDouble(p, "sumpElevation");
    var rimToSumpHeight = PluginRuntime.GetOptionalDouble(p, "rimToSumpHeight");

    if (new[] { sumpDepth.HasValue, sumpElevation.HasValue, rimToSumpHeight.HasValue }.Count(v => v) > 1)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give only one of sumpDepth, sumpElevation or rimToSumpHeight.");

    CivilSurface? surface = null;
    var useSurface = rimFromSurface == true || (!rimElevation.HasValue && rimFromSurface != false);
    if (rimElevation.HasValue && rimFromSurface == true)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give either rimElevation or rimFromSurface: true, not both.");
    if (useSurface)
    {
      surface = ResolveSurface(civilDoc, transaction, network, surfaceName);
      if (surface == null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Structure rim needs rimElevation or a surface ('surface', or a reference surface on network '{network.Name}'). An elevation of 0 will not be assumed.");
    }

    double insertZ = rimElevation ?? SurfaceElevation(surface!, x, y) + rimAdjustment;
    var createdId = ObjectId.Null;
    network.AddStructure(part.FamilyId, part.SizeId, new Point3d(x, y, insertZ), rotationDeg * Math.PI / 180.0, ref createdId, false);
    var structure = CivilObjectUtils.GetRequiredObject<Structure>(transaction, createdId, OpenMode.ForWrite);

    if (surface != null)
    {
      structure.RefSurfaceId = surface.ObjectId;
      structure.AutomaticRimSurfaceAdjustment = true;
      structure.SurfaceAdjustmentValue = rimAdjustment;
    }
    else
    {
      structure.AutomaticRimSurfaceAdjustment = false;
      structure.RimElevation = rimElevation!.Value;
      // Keep a surface reference for cover / surface-elevation reporting without letting it drive the rim.
      var reportSurface = Try(() => ResolveSurface(civilDoc, transaction, network, surfaceName));
      if (reportSurface != null) structure.RefSurfaceId = reportSurface.ObjectId;
    }

    if (sumpElevation.HasValue)
    {
      structure.ControlSumpBy = StructureControlSumpType.ByElevation;
      structure.SumpElevation = sumpElevation.Value;
    }
    else if (sumpDepth.HasValue)
    {
      structure.ControlSumpBy = StructureControlSumpType.ByDepth;
      structure.SumpDepth = sumpDepth.Value;
    }
    else if (rimToSumpHeight.HasValue)
    {
      structure.RimToSumpHeight = rimToSumpHeight.Value;
    }

    ApplyPartCommon(structure, p, civilDoc, transaction, warnings, DomainType.Structure);
    return createdId;
  }

  internal static ObjectId AddPipeCore(Network network, JsonObject? p, CivilDocument civilDoc, Database database, Transaction transaction, List<string> warnings)
  {
    var part = ResolvePartSize(network, transaction, PartNode(p), DomainType.Pipe, database);

    var startStructure = ResolveEndStructure(network, transaction, p, "startStructure");
    var endStructure = ResolveEndStructure(network, transaction, p, "endStructure");
    var startPoint = ReadPoint(p, "startPoint");
    var endPoint = ReadPoint(p, "endPoint");
    if (startStructure == null && startPoint == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give startStructure or startPoint.");
    if (endStructure == null && endPoint == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give endStructure or endPoint.");

    var sXY = startStructure != null ? new Point2d(startStructure.Location.X, startStructure.Location.Y) : new Point2d(startPoint!.Value.X, startPoint.Value.Y);
    var eXY = endStructure != null ? new Point2d(endStructure.Location.X, endStructure.Location.Y) : new Point2d(endPoint!.Value.X, endPoint.Value.Y);
    var length2d = sXY.GetDistanceTo(eXY);
    if (length2d < 1e-6)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "The pipe start and end are at the same XY location.");

    var startInvert = PluginRuntime.GetOptionalDouble(p, "startInvert");
    var endInvert = PluginRuntime.GetOptionalDouble(p, "endInvert");
    var slopePercent = PluginRuntime.GetOptionalDouble(p, "slope");
    var startCover = PluginRuntime.GetOptionalDouble(p, "startCover");
    var endCover = PluginRuntime.GetOptionalDouble(p, "endCover");
    var applyRules = PluginRuntime.GetOptionalBool(p, "applyRules") ?? false;
    var surfaceName = PluginRuntime.GetOptionalString(p, "surface");

    // --- work out what is given ---------------------------------------------------------------
    if (slopePercent.HasValue)
    {
      if (startInvert.HasValue && endInvert.HasValue)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give slope with one invert, not with both.");
      if (!startInvert.HasValue && !endInvert.HasValue && !startCover.HasValue && !endCover.HasValue)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "slope needs startInvert, endInvert, startCover or endCover to hang from.");
    }

    CivilSurface? surface = null;
    if (startCover.HasValue || endCover.HasValue)
    {
      surface = ResolveSurface(civilDoc, transaction, network, surfaceName)
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"startCover / endCover need a surface ('surface' or a reference surface on network '{network.Name}').");
    }

    var hasElevationInput = startInvert.HasValue || endInvert.HasValue || startCover.HasValue || endCover.HasValue;
    var legacyCentreline = !hasElevationInput && startPoint?.Z != null && endPoint?.Z != null;
    if (!hasElevationInput && !legacyCentreline && !applyRules)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        "No pipe elevations given. Use startInvert + endInvert, one invert + slope (%), startCover / endCover, or applyRules: true.");

    // --- create with a provisional line, read the real heights --------------------------------
    double provisionalZ = startStructure?.RimElevation ?? endStructure?.RimElevation ?? startPoint?.Z ?? 0.0;
    var createdId = ObjectId.Null;
    network.AddLinePipe(part.FamilyId, part.SizeId,
      new LineSegment3d(new Point3d(sXY.X, sXY.Y, provisionalZ - 1.0), new Point3d(eXY.X, eXY.Y, provisionalZ - 1.0)),
      ref createdId, false);
    var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, createdId, OpenMode.ForWrite);
    var innerHalf = pipe.InnerHeight / 2.0;
    var outerHalf = pipe.OuterHeight / 2.0;
    var coverSurface = surface ?? Try(() => ResolveSurface(civilDoc, transaction, network, surfaceName));
    if (coverSurface != null) pipe.RefSurfaceId = coverSurface.ObjectId;

    Point3d? targetStart = null, targetEnd = null;
    if (legacyCentreline)
    {
      targetStart = new Point3d(sXY.X, sXY.Y, startPoint!.Value.Z!.Value);
      targetEnd = new Point3d(eXY.X, eXY.Y, endPoint!.Value.Z!.Value);
    }
    else if (hasElevationInput)
    {
      double? sInv = startInvert, eInv = endInvert;
      if (!sInv.HasValue && startCover.HasValue) sInv = SurfaceElevation(surface!, sXY.X, sXY.Y) - startCover.Value - outerHalf - innerHalf;
      if (!eInv.HasValue && endCover.HasValue) eInv = SurfaceElevation(surface!, eXY.X, eXY.Y) - endCover.Value - outerHalf - innerHalf;
      if (slopePercent.HasValue)
      {
        if (sInv.HasValue && !eInv.HasValue) eInv = sInv.Value - slopePercent.Value / 100.0 * length2d;
        else if (eInv.HasValue && !sInv.HasValue) sInv = eInv.Value + slopePercent.Value / 100.0 * length2d;
        else if (sInv.HasValue && eInv.HasValue) warnings.Add("slope was ignored because both end elevations were already fixed (invert or cover at each end).");
      }
      if (!sInv.HasValue || !eInv.HasValue)
      {
        pipe.Erase();
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Only one end elevation could be worked out. Give the other invert, a cover at the other end, or a slope.");
      }
      targetStart = new Point3d(sXY.X, sXY.Y, sInv.Value + innerHalf);
      targetEnd = new Point3d(eXY.X, eXY.Y, eInv.Value + innerHalf);
    }

    if (targetStart.HasValue)
    {
      pipe.StartPoint = targetStart.Value;
      pipe.EndPoint = targetEnd!.Value;
    }

    // --- connect ------------------------------------------------------------------------------
    if (startStructure != null) pipe.ConnectToStructure(ConnectorPositionType.Start, startStructure.ObjectId, true);
    if (endStructure != null) pipe.ConnectToStructure(ConnectorPositionType.End, endStructure.ObjectId, true);

    if (targetStart.HasValue)
    {
      var moved = Math.Abs(pipe.StartPoint.Z - targetStart.Value.Z) > 1e-6 || Math.Abs(pipe.EndPoint.Z - targetEnd!.Value.Z) > 1e-6;
      if (moved)
      {
        var sp = pipe.StartPoint; var ep = pipe.EndPoint;
        pipe.StartPoint = new Point3d(sp.X, sp.Y, targetStart.Value.Z);
        pipe.EndPoint = new Point3d(ep.X, ep.Y, targetEnd!.Value.Z);
        warnings.Add("Connecting moved the pipe's end elevations; the requested elevations were re-applied.");
      }
    }
    else if (applyRules)
    {
      pipe.ApplyRules();
    }

    ApplyPartCommon(pipe, p, civilDoc, transaction, warnings, DomainType.Pipe);

    var flow = PluginRuntime.GetOptionalString(p, "flowDirection");
    if (!string.IsNullOrWhiteSpace(flow))
      pipe.FlowDirectionMethod = ParseFlowDirection(flow!);

    var sInvert = pipe.StartPoint.Z - innerHalf;
    var eInvert = pipe.EndPoint.Z - innerHalf;
    if (eInvert > sInvert + 1e-6)
      warnings.Add($"The pipe rises from start ({R(sInvert)}) to end ({R(eInvert)}); check the flow direction.");
    CheckCover(pipe, warnings);
    return createdId;
  }

  // =============================================================================================
  // Data shapes
  // =============================================================================================

  private static Dictionary<string, object?> ToNetworkSummary(Network network, Transaction transaction)
  {
    var pipeIds = network.GetPipeIds();
    double total = 0;
    foreach (ObjectId id in pipeIds)
      total += Try(() => CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead).Length2D) ?? 0;
    return new Dictionary<string, object?>
    {
      ["name"] = network.Name,
      ["handle"] = CivilObjectUtils.GetHandle(network),
      ["partsList"] = ResolveObjectName(transaction, network.PartsListId),
      ["referenceSurface"] = ResolveObjectName(transaction, network.ReferenceSurfaceId),
      ["referenceAlignment"] = ResolveObjectName(transaction, network.ReferenceAlignmentId),
      ["pipeCount"] = pipeIds.Count,
      ["structureCount"] = network.GetStructureIds().Count,
      ["totalPipeLength2D"] = R(total),
    };
  }

  private static Dictionary<string, object?> ToNetworkDetail(Network network, Transaction transaction, bool includeParts)
  {
    var detail = ToNetworkSummary(network, transaction);
    detail["pipeNameTemplate"] = Try(() => network.PipeNameTemplate);
    detail["structureNameTemplate"] = Try(() => network.StructureNameTemplate);
    detail["labelStyles"] = new Dictionary<string, object?>
    {
      ["pipePlan"] = Try(() => network.PipePlanLabelStyleName),
      ["pipeProfile"] = Try(() => network.PipeProfileLabelStyleName),
      ["structurePlan"] = Try(() => network.StructurePlanLabelStyleName),
      ["structureProfile"] = Try(() => network.StructureProfileLabelStyleName),
    };
    detail["layers"] = new Dictionary<string, object?>
    {
      ["pipePlan"] = Try(() => network.PipePlanLayerName),
      ["pipeProfile"] = Try(() => network.PipeProfileLayerName),
      ["structurePlan"] = Try(() => network.StructurePlanLayerName),
      ["structureProfile"] = Try(() => network.StructureProfileLayerName),
      ["section"] = Try(() => network.PipeNetworkSectionLayerName),
    };
    if (includeParts)
    {
      detail["structures"] = network.GetStructureIds().Cast<ObjectId>()
        .Select(id => ToStructureData(CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead), transaction))
        .ToList();
      detail["pipes"] = network.GetPipeIds().Cast<ObjectId>()
        .Select(id => ToPipeData(CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead), transaction))
        .ToList();
    }
    return detail;
  }

  internal static Dictionary<string, object?> ToPipeData(Pipe pipe, Transaction transaction)
  {
    var innerHalf = Try(() => pipe.InnerHeight / 2.0) ?? 0.0;
    var outerHalf = Try(() => pipe.OuterHeight / 2.0) ?? innerHalf;
    var sp = pipe.StartPoint;
    var ep = pipe.EndPoint;
    var length2d = new Point2d(sp.X, sp.Y).GetDistanceTo(new Point2d(ep.X, ep.Y));
    var sInv = sp.Z - innerHalf;
    var eInv = ep.Z - innerHalf;

    Dictionary<string, object?> End(Point3d pt, ObjectId structureId, Func<double> cover) => new()
    {
      ["structure"] = ResolveObjectName(transaction, structureId),
      ["x"] = R(pt.X),
      ["y"] = R(pt.Y),
      ["centerline"] = R(pt.Z),
      ["invert"] = R(pt.Z - innerHalf),
      ["crown"] = R(pt.Z + innerHalf),
      ["outerTop"] = R(pt.Z + outerHalf),
      ["cover"] = Round(Try(cover)),
    };

    return new Dictionary<string, object?>
    {
      ["name"] = pipe.Name,
      ["handle"] = CivilObjectUtils.GetHandle(pipe),
      ["network"] = Try(() => pipe.NetworkName),
      ["partFamily"] = Try(() => pipe.PartFamilyName),
      ["partSize"] = Try(() => pipe.PartSizeName),
      ["description"] = Try(() => pipe.PartDescription),
      ["material"] = Try(() => pipe.Material),
      ["shape"] = Try(() => pipe.CrossSectionalShape.ToString()),
      ["innerDiameterOrWidth"] = Round(Try(() => pipe.InnerDiameterOrWidth)),
      ["innerHeight"] = Round(Try(() => pipe.InnerHeight)),
      ["outerDiameterOrWidth"] = Round(Try(() => pipe.OuterDiameterOrWidth)),
      ["wallThickness"] = Round(Try(() => pipe.WallThickness)),
      ["start"] = End(sp, pipe.StartStructureId, () => pipe.CoverOfStartPoint),
      ["end"] = End(ep, pipe.EndStructureId, () => pipe.CoverOfEndpoint),
      ["length2D"] = R(length2d),
      ["length3D"] = Round(Try(() => pipe.Length3D)),
      ["length2DCenterToCenter"] = Round(Try(() => pipe.Length2DCenterToCenter)),
      ["length2DToInsideEdge"] = Round(Try(() => pipe.Length2DToInsideEdge)),
      ["slopePercent"] = length2d > 1e-9 ? R((sInv - eInv) / length2d * 100.0) : null,
      ["slopeOneIn"] = length2d > 1e-9 && Math.Abs(sInv - eInv) > 1e-9 ? Math.Round(length2d / Math.Abs(sInv - eInv), 1) : null,
      ["apiSlope"] = Round(Try(() => pipe.Slope)),
      ["minimumCover"] = Round(Try(() => pipe.MinimumCover)),
      ["maximumCover"] = Round(Try(() => pipe.MaximumCover)),
      ["flowDirectionMethod"] = Try(() => pipe.FlowDirectionMethod.ToString()),
      ["flowDirection"] = Try(() => pipe.FlowDirection.ToString()),
      ["holdOnResize"] = Try(() => pipe.HoldOnResizeType.ToString()),
      ["style"] = Try(() => ((CivilEntity)pipe).StyleName),
      ["ruleSet"] = Try(() => pipe.RuleSetStyleName),
      ["referenceSurface"] = Try(() => pipe.RefSurfaceName),
      ["referenceAlignment"] = Try(() => pipe.RefAlignmentName),
      ["isCurved"] = Try(() => pipe.SubEntityType.ToString()),
    };
  }

  internal static Dictionary<string, object?> ToStructureData(Structure structure, Transaction transaction)
  {
    var connected = new List<Dictionary<string, object?>>();
    var count = Try(() => structure.ConnectedPipesCount) ?? 0;
    for (var i = 0; i < count; i++)
    {
      var index = i;
      var pipeId = Try(() => structure.get_ConnectedPipe(index)) ?? ObjectId.Null;
      if (pipeId.IsNull) continue;
      var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, pipeId, OpenMode.ForRead);
      var atStart = pipe.StartStructureId == structure.ObjectId;
      var pt = atStart ? pipe.StartPoint : pipe.EndPoint;
      var innerHalf = Try(() => pipe.InnerHeight / 2.0) ?? 0.0;
      connected.Add(new Dictionary<string, object?>
      {
        ["pipe"] = pipe.Name,
        ["handle"] = CivilObjectUtils.GetHandle(pipe),
        ["pipeEnd"] = atStart ? "start" : "end",
        ["flow"] = Try(() => structure.IsConnectedPipeFlowingIn(index)) == true ? "in"
                 : Try(() => structure.IsConnectedPipeFlowingOut(index)) == true ? "out" : "unknown",
        ["invert"] = R(pt.Z - innerHalf),
        ["crown"] = R(pt.Z + innerHalf),
        ["invertDepth"] = Round(Try(() => structure.get_PipeInvertDepth(index))),
        ["innerDiameterOrWidth"] = Round(Try(() => pipe.InnerDiameterOrWidth)),
        ["partSize"] = Try(() => pipe.PartSizeName),
      });
    }

    var rim = Try(() => structure.RimElevation);
    var sump = Try(() => structure.SumpElevation);
    return new Dictionary<string, object?>
    {
      ["name"] = structure.Name,
      ["handle"] = CivilObjectUtils.GetHandle(structure),
      ["network"] = Try(() => structure.NetworkName),
      ["partFamily"] = Try(() => structure.PartFamilyName),
      ["partSize"] = Try(() => structure.PartSizeName),
      ["description"] = Try(() => structure.PartDescription),
      ["partType"] = Try(() => structure.PartType.ToString()),
      ["structureType"] = Try(() => structure.StructureType.ToString()),
      ["boundingShape"] = Try(() => structure.BoundingShape.ToString()),
      ["x"] = R(structure.Location.X),
      ["y"] = R(structure.Location.Y),
      ["rotation"] = Round(Try(() => structure.Rotation * 180.0 / Math.PI)),
      ["rimElevation"] = Round(rim),
      ["sumpElevation"] = Round(sump),
      ["depth"] = rim.HasValue && sump.HasValue ? R(rim.Value - sump.Value) : null,
      ["sumpDepth"] = Round(Try(() => structure.SumpDepth)),
      ["controlSumpBy"] = Try(() => structure.ControlSumpBy.ToString()),
      ["rimFollowsSurface"] = Try(() => structure.AutomaticRimSurfaceAdjustment),
      ["surfaceAdjustment"] = Round(Try(() => structure.SurfaceAdjustmentValue)),
      ["surfaceElevation"] = Round(Try(() => structure.SurfaceElevationAtInsertionPoint)),
      ["referenceSurface"] = Try(() => structure.RefSurfaceName),
      ["station"] = Round(Try(() => structure.Station)),
      ["offset"] = Round(Try(() => structure.Offset)),
      ["diameterOrWidth"] = Round(Try(() => structure.DiameterOrWidth)),
      ["innerDiameterOrWidth"] = Round(Try(() => structure.InnerDiameterOrWidth)),
      ["length"] = Round(Try(() => structure.Length)),
      ["innerLength"] = Round(Try(() => structure.InnerLength)),
      ["height"] = Round(Try(() => structure.Height)),
      ["style"] = Try(() => ((CivilEntity)structure).StyleName),
      ["ruleSet"] = Try(() => structure.RuleSetStyleName),
      ["connectedPipes"] = connected,
    };
  }

  private static List<Dictionary<string, object?>> DescribeFamilies(PartsList partsList, DomainType domain, Transaction transaction, bool includeFields, double lengthFactor)
  {
    var families = new List<Dictionary<string, object?>>();
    ObjectIdCollection ids;
    try { ids = partsList.GetPartFamilyIdsByDomain(domain); } catch { return families; }
    foreach (ObjectId familyId in ids)
    {
      var family = CivilObjectUtils.GetRequiredObject<PartFamily>(transaction, familyId, OpenMode.ForRead);
      var sizes = new List<Dictionary<string, object?>>();
      for (var i = 0; i < family.PartSizeCount; i++)
      {
        var sizeId = family[i];
        var size = CivilObjectUtils.GetRequiredObject<PartSize>(transaction, sizeId, OpenMode.ForRead);
        var entry = new Dictionary<string, object?>
        {
          ["index"] = i,
          ["name"] = SizeName(size, family),
          ["style"] = ResolveObjectName(transaction, Try(() => size.PartStyleId) ?? ObjectId.Null),
          ["rules"] = ResolveObjectName(transaction, Try(() => size.RulesStyleId) ?? ObjectId.Null),
          ["payItems"] = Try(() => size.PayItems),
        };
        var record = Try(() => size.SizeDataRecord);
        if (record != null)
        {
          if (domain == DomainType.Pipe)
          {
            entry["innerDiameter"] = FieldLength(record, PartContextType.PipeInnerDiameter, lengthFactor);
            entry["innerWidth"] = FieldLength(record, PartContextType.PipeInnerWidth, lengthFactor);
            entry["innerHeight"] = FieldLength(record, PartContextType.PipeInnerHeight, lengthFactor);
            entry["wallThickness"] = FieldLength(record, PartContextType.WallThickness, lengthFactor);
          }
          else
          {
            entry["diameter"] = FieldLength(record, PartContextType.StructDiameter, lengthFactor);
            entry["innerDiameter"] = FieldLength(record, PartContextType.StructInnerDiameter, lengthFactor);
            entry["width"] = FieldLength(record, PartContextType.StructWidth, lengthFactor);
            entry["length"] = FieldLength(record, PartContextType.StructLength, lengthFactor);
            entry["height"] = FieldLength(record, PartContextType.StructHeight, lengthFactor);
          }
          entry["material"] = FieldValue(record, PartContextType.Material);
          if (includeFields) entry["fields"] = DescribeFields(record);
        }
        sizes.Add(entry);
      }
      families.Add(new Dictionary<string, object?>
      {
        ["name"] = family.Description,
        ["guid"] = family.GUID,
        ["partType"] = family.PartType.ToString(),
        ["sweptShape"] = Try(() => family.SweptShape.ToString()),
        ["boundingShape"] = Try(() => family.BoundingShape.ToString()),
        ["sizes"] = sizes,
      });
    }
    return families;
  }

  private static List<Dictionary<string, object?>> DescribeCatalogFamilies(DomainType domain)
  {
    try
    {
      return PartsList.GetAvailablePartFamilies(domain)
        .Select(f => new Dictionary<string, object?>
        {
          ["name"] = f.Description,
          ["guid"] = f.GUID,
          ["partType"] = f.PartType.ToString(),
          ["sweptShape"] = f.SweptShape.ToString(),
          ["boundingShape"] = f.BoundingShape.ToString(),
        })
        .ToList();
    }
    catch (Exception ex)
    {
      return new List<Dictionary<string, object?>> { new() { ["error"] = ex.Message } };
    }
  }

  private static List<Dictionary<string, object?>> DescribeFields(PartDataRecord record)
  {
    var fields = new List<Dictionary<string, object?>>();
    PartDataField[] all;
    try { all = record.GetAllDataFields(); } catch { return fields; }
    foreach (var f in all)
    {
      fields.Add(new Dictionary<string, object?>
      {
        ["name"] = Try(() => f.Name),
        ["description"] = Try(() => f.Description),
        ["context"] = Try(() => f.ContextString),
        ["value"] = JsonSafe(Try(() => f.Value)),
        ["units"] = Try(() => f.Units),
        ["readOnly"] = Try(() => f.IsReadOnly),
      });
    }
    return fields;
  }

  // =============================================================================================
  // Resolution helpers
  // =============================================================================================

  private readonly record struct PartIds(ObjectId FamilyId, ObjectId SizeId, string FamilyName, string? SizeName);

  private static JsonNode? PartNode(JsonObject? p) =>
    (PluginRuntime.GetParameter(p, "part") as JsonNode) ?? (PluginRuntime.GetParameter(p, "partName") as JsonNode);

  /// <summary>
  /// Part from the network's parts list. Accepts a size name ("600 mm Concrete Pipe") or
  /// { family, size } / { family, innerDiameter } / { family, diameter } (lengths in drawing units).
  /// </summary>
  private static PartIds ResolvePartSize(Network network, Transaction transaction, JsonNode? node, DomainType domain, Database database)
  {
    if (node == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'part' is required (a {domain.ToString().ToLowerInvariant()} size name, or {{family, size}}).");
    if (network.PartsListId.IsNull)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Pipe network '{network.Name}' has no parts list.");

    string? sizeName = null, familyName = null;
    double? diameter = null;
    if (node is JsonValue v && v.TryGetValue<string>(out var s)) sizeName = s;
    else if (node is JsonObject o)
    {
      familyName = o["family"]?.GetValue<string>();
      sizeName = o["size"]?.GetValue<string>();
      diameter = o["innerDiameter"]?.GetValue<double>() ?? o["diameter"]?.GetValue<double>();
    }
    if (string.IsNullOrWhiteSpace(sizeName) && string.IsNullOrWhiteSpace(familyName))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "'part' needs a size name, or a family with size / innerDiameter.");

    var factor = CatalogToDrawingFactor(database);
    var partsList = CivilObjectUtils.GetRequiredObject<PartsList>(transaction, network.PartsListId, OpenMode.ForRead);
    var available = new List<string>();
    PartIds? byDiameter = null;
    double bestDelta = double.MaxValue;

    foreach (ObjectId familyId in partsList.GetPartFamilyIdsByDomain(domain))
    {
      var family = CivilObjectUtils.GetRequiredObject<PartFamily>(transaction, familyId, OpenMode.ForRead);
      if (!string.IsNullOrWhiteSpace(familyName) && !string.Equals(family.Description, familyName, StringComparison.OrdinalIgnoreCase))
        continue;
      for (var i = 0; i < family.PartSizeCount; i++)
      {
        var sizeId = family[i];
        var size = CivilObjectUtils.GetRequiredObject<PartSize>(transaction, sizeId, OpenMode.ForRead);
        var name = SizeName(size, family);
        available.Add(string.IsNullOrWhiteSpace(familyName) ? $"{name} [{family.Description}]" : name ?? "?");
        if (!string.IsNullOrWhiteSpace(sizeName) && string.Equals(name, sizeName, StringComparison.OrdinalIgnoreCase))
          return new PartIds(familyId, sizeId, family.Description, name);
        if (string.IsNullOrWhiteSpace(sizeName) && !string.IsNullOrWhiteSpace(familyName) && family.PartSizeCount == 1 && !diameter.HasValue)
          return new PartIds(familyId, sizeId, family.Description, name);
        if (diameter.HasValue)
        {
          var record = Try(() => size.SizeDataRecord);
          var d = record == null ? null : domain == DomainType.Pipe
            ? FieldLength(record, PartContextType.PipeInnerDiameter, factor) ?? FieldLength(record, PartContextType.PipeInnerWidth, factor)
            : FieldLength(record, PartContextType.StructInnerDiameter, factor) ?? FieldLength(record, PartContextType.StructDiameter, factor) ?? FieldLength(record, PartContextType.StructInnerWidth, factor);
          if (d.HasValue && Math.Abs(d.Value - diameter.Value) < bestDelta)
          {
            bestDelta = Math.Abs(d.Value - diameter.Value);
            byDiameter = new PartIds(familyId, sizeId, family.Description, name);
          }
        }
      }
    }

    if (byDiameter.HasValue && bestDelta < 1e-4)
      return byDiameter.Value;

    var what = !string.IsNullOrWhiteSpace(sizeName) ? $"size '{sizeName}'" : $"a {diameter} diameter size";
    var inFamily = string.IsNullOrWhiteSpace(familyName) ? "" : $" in family '{familyName}'";
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
      $"No {domain.ToString().ToLowerInvariant()} {what}{inFamily} in parts list '{CivilObjectUtils.GetName(partsList)}'. Available: {string.Join("; ", available.Take(80))}.");
  }

  /// <summary>
  /// The display name of a part size. The size DBObject has no Name; the catalog stores it in the
  /// "Part Size Name" parameter (PrtSN). Falls back to the part description, then family + index.
  /// </summary>
  private static string? SizeName(PartSize size, PartFamily family)
  {
    var record = Try(() => size.SizeDataRecord);
    if (record != null)
    {
      foreach (var key in new[] { "PrtSN", "PartSizeName", "PrtD", "PartDesc" })
      {
        var value = Try(() => record.GetDataFieldBy(key)?.Value as string);
        if (!string.IsNullOrWhiteSpace(value)) return value;
      }
      var fields = Try(() => record.GetAllDataFields());
      if (fields != null)
      {
        foreach (var f in fields)
        {
          var desc = Try(() => f.Description) ?? "";
          if ((desc.Contains("Size Name", StringComparison.OrdinalIgnoreCase) || desc.Contains("Part Description", StringComparison.OrdinalIgnoreCase))
              && Try(() => f.Value) is string text && !string.IsNullOrWhiteSpace(text))
            return text;
        }
      }
    }
    return CivilObjectUtils.GetName(size);
  }

  private static object? FieldValue(PartDataRecord record, PartContextType context)
  {
    return JsonSafe(Try(() => record.GetDataFieldBy(context)?.Value));
  }

  /// <summary>A length field converted from catalog units to drawing units.</summary>
  private static double? FieldLength(PartDataRecord record, PartContextType context, double drawingPerMetre)
  {
    var field = Try(() => record.GetDataFieldBy(context));
    if (field == null) return null;
    var raw = Try(() => Convert.ToDouble(field.Value));
    if (!raw.HasValue) return null;
    var units = (Try(() => field.Units) ?? "").Trim().ToLowerInvariant();
    double metres = units switch
    {
      "mm" or "millimeter" or "millimeters" or "millimetre" or "millimetres" => raw.Value / 1000.0,
      "cm" or "centimeter" or "centimeters" => raw.Value / 100.0,
      "in" or "inch" or "inches" => raw.Value * 0.0254,
      "ft" or "foot" or "feet" => raw.Value * 0.3048,
      _ => raw.Value, // "m", "" — already metres
    };
    return R(metres * drawingPerMetre);
  }

  /// <summary>Drawing units per metre (1 for metric drawings, 3.28084 for feet).</summary>
  private static double CatalogToDrawingFactor(Database database)
  {
    var units = CivilObjectUtils.LinearUnits(database);
    return string.Equals(units, "feet", StringComparison.OrdinalIgnoreCase) ? 1.0 / 0.3048 : 1.0;
  }

  private static void ApplyNetworkSettings(Network network, JsonObject? p, CivilDocument civilDoc, Database database, Transaction transaction, List<string> warnings)
  {
    var surfaceName = PluginRuntime.GetOptionalString(p, "referenceSurface");
    if (surfaceName != null)
      network.ReferenceSurfaceId = surfaceName.Length == 0 ? ObjectId.Null : CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName, OpenMode.ForRead).ObjectId;

    var alignmentName = PluginRuntime.GetOptionalString(p, "referenceAlignment");
    if (alignmentName != null)
      network.ReferenceAlignmentId = alignmentName.Length == 0 ? ObjectId.Null : CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName).ObjectId;

    var pipeTemplate = PluginRuntime.GetOptionalString(p, "pipeNameTemplate");
    if (!string.IsNullOrWhiteSpace(pipeTemplate)) network.PipeNameTemplate = pipeTemplate;
    var structureTemplate = PluginRuntime.GetOptionalString(p, "structureNameTemplate");
    if (!string.IsNullOrWhiteSpace(structureTemplate)) network.StructureNameTemplate = structureTemplate;

    var labels = civilDoc.Styles.LabelStyles;
    var pipePlan = PluginRuntime.GetOptionalString(p, "pipePlanLabelStyle");
    if (!string.IsNullOrWhiteSpace(pipePlan))
      network.PipePlanLabelStyleId = FindStyleStrict(labels.PipeLabelStyles.PlanProfileLabelStyles, transaction, pipePlan!, "Pipe label style");
    var pipeProfile = PluginRuntime.GetOptionalString(p, "pipeProfileLabelStyle");
    if (!string.IsNullOrWhiteSpace(pipeProfile))
      network.PipeProfileLabelStyleId = FindStyleStrict(labels.PipeLabelStyles.PlanProfileLabelStyles, transaction, pipeProfile!, "Pipe label style");
    var structurePlan = PluginRuntime.GetOptionalString(p, "structurePlanLabelStyle");
    if (!string.IsNullOrWhiteSpace(structurePlan))
      network.StructurePlanLabelStyleId = FindStyleStrict(labels.StructureLabelStyles.LabelStyles, transaction, structurePlan!, "Structure label style");
    var structureProfile = PluginRuntime.GetOptionalString(p, "structureProfileLabelStyle");
    if (!string.IsNullOrWhiteSpace(structureProfile))
      network.StructureProfileLabelStyleId = FindStyleStrict(labels.StructureLabelStyles.LabelStyles, transaction, structureProfile!, "Structure label style");

    // `layer` (legacy) = the plan layer for both pipes and structures.
    var legacyLayer = PluginRuntime.GetOptionalString(p, "layer");
    SetLayer(PluginRuntime.GetOptionalString(p, "pipePlanLayer") ?? legacyLayer, l => network.PipePlanLayerName = l, database, transaction);
    SetLayer(PluginRuntime.GetOptionalString(p, "structurePlanLayer") ?? legacyLayer, l => network.StructurePlanLayerName = l, database, transaction);
    SetLayer(PluginRuntime.GetOptionalString(p, "pipeProfileLayer"), l => network.PipeProfileLayerName = l, database, transaction);
    SetLayer(PluginRuntime.GetOptionalString(p, "structureProfileLayer"), l => network.StructureProfileLayerName = l, database, transaction);
    SetLayer(PluginRuntime.GetOptionalString(p, "sectionLayer"), l => network.PipeNetworkSectionLayerName = l, database, transaction);

    if (!string.IsNullOrWhiteSpace(PluginRuntime.GetOptionalString(p, "style")))
      warnings.Add("Pipe networks have no style of their own; 'style' was ignored. Set pipe / structure styles per part (pipeStyle / structureStyle on add_pipe / add_structure) or in the parts list.");
  }

  private static void ApplyPartCommon(Part part, JsonObject? p, CivilDocument civilDoc, Transaction transaction, List<string> warnings, DomainType domain)
  {
    var name = PluginRuntime.GetOptionalString(p, "name");
    if (!string.IsNullOrWhiteSpace(name))
    {
      try { ((CivilEntity)part).Name = name!; }
      catch (Exception ex) { warnings.Add($"Could not rename the part to '{name}': {ex.Message}"); }
    }

    var description = PluginRuntime.GetOptionalString(p, "description");
    if (!string.IsNullOrWhiteSpace(description))
    {
      try { ((CivilEntity)part).Description = description!; }
      catch (Exception ex) { warnings.Add($"Could not set the description: {ex.Message}"); }
    }

    var styleName = PluginRuntime.GetOptionalString(p, "style");
    if (!string.IsNullOrWhiteSpace(styleName))
    {
      object collection = domain == DomainType.Pipe ? civilDoc.Styles.PipeStyles : civilDoc.Styles.StructureStyles;
      ((CivilEntity)part).StyleId = FindStyleStrict(collection, transaction, styleName!, domain == DomainType.Pipe ? "Pipe style" : "Structure style");
    }

    var ruleSet = PluginRuntime.GetOptionalString(p, "ruleSet");
    if (!string.IsNullOrWhiteSpace(ruleSet))
    {
      object collection = domain == DomainType.Pipe ? civilDoc.Styles.PipeRuleSetStyles : civilDoc.Styles.StructureRuleSetStyles;
      part.OverrideRuleSet = true;
      part.RuleSetStyleId = FindStyleStrict(collection, transaction, ruleSet!, domain == DomainType.Pipe ? "Pipe rule set" : "Structure rule set");
    }

    if (domain == DomainType.Structure && PluginRuntime.GetOptionalBool(p, "applyRules") == true)
    {
      try { part.ApplyRules(); }
      catch (Exception ex) { warnings.Add($"ApplyRules failed: {ex.Message}"); }
    }
  }

  private static Structure? ResolveEndStructure(Network network, Transaction transaction, JsonObject? p, string key)
  {
    var value = PluginRuntime.GetOptionalString(p, key);
    return string.IsNullOrWhiteSpace(value) ? null : FindStructure(network, transaction, value!, OpenMode.ForRead);
  }

  private static CivilSurface? ResolveSurface(CivilDocument civilDoc, Transaction transaction, Network network, string? surfaceName)
  {
    if (!string.IsNullOrWhiteSpace(surfaceName))
      return CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName!, OpenMode.ForRead);
    if (!network.ReferenceSurfaceId.IsNull)
      return CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, network.ReferenceSurfaceId, OpenMode.ForRead);
    return null;
  }

  private static double SurfaceElevation(CivilSurface surface, double x, double y)
  {
    try { return surface.FindElevationAtXY(x, y); }
    catch (Exception ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Surface '{surface.Name}' has no elevation at ({x:F3}, {y:F3}): {ex.Message}");
    }
  }

  private static void CheckCover(Pipe pipe, List<string> warnings)
  {
    var sc = Try(() => pipe.CoverOfStartPoint);
    var ec = Try(() => pipe.CoverOfEndpoint);
    if (sc.HasValue && sc.Value < 0) warnings.Add($"The pipe start is above the surface (cover {R(sc.Value)}).");
    if (ec.HasValue && ec.Value < 0) warnings.Add($"The pipe end is above the surface (cover {R(ec.Value)}).");
  }

  private static FlowDirectionMethodType ParseFlowDirection(string value)
  {
    return value.Replace("_", "").Replace("-", "").ToLowerInvariant() switch
    {
      "byslope" => FlowDirectionMethodType.BySlope,
      "starttoend" => FlowDirectionMethodType.StartToEnd,
      "endtostart" => FlowDirectionMethodType.EndToStart,
      "bidirectional" => FlowDirectionMethodType.Bidirectional,
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown flowDirection '{value}'. Use by_slope, start_to_end, end_to_start or bidirectional."),
    };
  }

  private static ObjectId FindStyleStrict(object collection, Transaction transaction, string name, string kind)
  {
    var names = new List<string>();
    foreach (var id in CivilObjectUtils.ToObjectIds(collection))
    {
      var styleName = CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead));
      if (styleName == null) continue;
      if (string.Equals(styleName, name, StringComparison.OrdinalIgnoreCase)) return id;
      names.Add(styleName);
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"{kind} '{name}' was not found. Available: {string.Join(", ", names)}.");
  }

  private static void SetLayer(string? layer, Action<string> assign, Database database, Transaction transaction)
  {
    if (string.IsNullOrWhiteSpace(layer)) return;
    var table = CivilObjectUtils.GetRequiredObject<LayerTable>(transaction, database.LayerTableId, OpenMode.ForRead);
    if (!table.Has(layer))
    {
      table.UpgradeOpen();
      var record = new LayerTableRecord { Name = layer };
      table.Add(record);
      transaction.AddNewlyCreatedDBObject(record, true);
    }
    assign(layer!);
  }

  private static IEnumerable<Network> EnumeratePipeNetworks(object civilDoc, Transaction transaction, OpenMode openMode)
  {
    foreach (ObjectId objectId in ((CivilDocument)civilDoc).GetPipeNetworkIds())
      yield return CivilObjectUtils.GetRequiredObject<Network>(transaction, objectId, openMode);
  }

  private static IEnumerable<PartsList> EnumeratePartsLists(CivilDocument civilDoc, Transaction transaction)
  {
    foreach (var id in CivilObjectUtils.ToObjectIds(civilDoc.Styles.PartsListSet))
      yield return CivilObjectUtils.GetRequiredObject<PartsList>(transaction, id, OpenMode.ForRead);
  }

  private static ObjectId FindPartsListId(CivilDocument civilDoc, Transaction transaction, string partsListName)
  {
    var names = new List<string>();
    foreach (var partsList in EnumeratePartsLists(civilDoc, transaction))
    {
      var name = CivilObjectUtils.GetName(partsList) ?? "";
      if (string.Equals(name, partsListName, StringComparison.OrdinalIgnoreCase)) return partsList.ObjectId;
      names.Add(name);
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Parts list '{partsListName}' was not found. Available: {string.Join(", ", names)}.");
  }

  internal static Network FindPipeNetworkByName(object civilDoc, Transaction transaction, string name, OpenMode openMode)
  {
    var names = new List<string>();
    foreach (var network in EnumeratePipeNetworks(civilDoc, transaction, openMode))
    {
      if (string.Equals(network.Name, name, StringComparison.OrdinalIgnoreCase)) return network;
      names.Add(network.Name);
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe network '{name}' was not found. Available: {(names.Count == 0 ? "(none)" : string.Join(", ", names))}.");
  }

  /// <summary>A pipe by name or handle.</summary>
  internal static Pipe FindPipe(Network network, Transaction transaction, string key, OpenMode openMode)
  {
    foreach (ObjectId id in network.GetPipeIds())
    {
      var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead);
      if (string.Equals(pipe.Name, key, StringComparison.OrdinalIgnoreCase) || string.Equals(pipe.Handle.ToString(), key, StringComparison.OrdinalIgnoreCase))
      {
        if (openMode == OpenMode.ForWrite) pipe.UpgradeOpen();
        return pipe;
      }
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe '{key}' was not found in network '{network.Name}'.");
  }

  /// <summary>A structure by name or handle.</summary>
  internal static Structure FindStructure(Network network, Transaction transaction, string key, OpenMode openMode)
  {
    foreach (ObjectId id in network.GetStructureIds())
    {
      var structure = CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead);
      if (string.Equals(structure.Name, key, StringComparison.OrdinalIgnoreCase) || string.Equals(structure.Handle.ToString(), key, StringComparison.OrdinalIgnoreCase))
      {
        if (openMode == OpenMode.ForWrite) structure.UpgradeOpen();
        return structure;
      }
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Structure '{key}' was not found in network '{network.Name}'.");
  }

  internal static Part FindPart(Network network, Transaction transaction, string key, OpenMode openMode)
  {
    try { return FindPipe(network, transaction, key, openMode); }
    catch (JsonRpcDispatchException) { }
    try { return FindStructure(network, transaction, key, openMode); }
    catch (JsonRpcDispatchException) { }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No pipe or structure '{key}' in network '{network.Name}'.");
  }

  private static string? ResolveObjectName(Transaction transaction, ObjectId objectId)
  {
    if (objectId.IsNull) return null;
    try { return CivilObjectUtils.GetName(transaction.GetObject(objectId, OpenMode.ForRead)); }
    catch { return null; }
  }

  private readonly record struct InputPoint(double X, double Y, double? Z);

  private static InputPoint? ReadPoint(JsonObject? parameters, string name)
  {
    if (PluginRuntime.GetParameter(parameters, name) is not JsonObject node) return null;
    var x = node["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{name}.x is required.");
    var y = node["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{name}.y is required.");
    var z = node["z"]?.GetValue<double>();
    return new InputPoint(x, y, z);
  }

  private static T? Try<T>(Func<T> read) where T : class?
  {
    try { return read(); } catch { return null; }
  }

  private static T? Try<T>(Func<T> read, bool _ = false) where T : struct
  {
    try { return read(); } catch { return null; }
  }

  private static object? JsonSafe(object? value) => value switch
  {
    null => null,
    string or bool or int or long or short or double or float or decimal => value,
    _ => value.ToString(),
  };

  private static double R(double value) => Math.Round(value, Digits);
  private static double? Round(double? value) => value.HasValue ? Math.Round(value.Value, Digits) : null;
}
