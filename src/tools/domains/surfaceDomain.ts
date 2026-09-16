import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

const Point2DSchema = z.object({
  x: z.number(),
  y: z.number(),
});

const Point3DSchema = z.object({
  x: z.number(),
  y: z.number(),
  z: z.number(),
});

const Point2DOr3DSchema = z.union([Point3DSchema, Point2DSchema]);

const GenericSurfaceResponseSchema = z.object({}).passthrough();

const SurfaceSummarySchema = z.object({
  name: z.string(),
  handle: z.string(),
  type: z.enum(["TIN", "Grid", "TINVolume"]),
  isReference: z.boolean(),
  sourcePath: z.string().nullable(),
});

const SurfaceListResponseSchema = z.object({
  surfaces: z.array(SurfaceSummarySchema),
});

const SurfaceDetailResponseSchema = z.object({
  name: z.string(),
  handle: z.string(),
  type: z.enum(["TIN", "Grid", "TINVolume"]),
  style: z.string(),
  layer: z.string(),
  statistics: z.object({
    minimumElevation: z.number(),
    maximumElevation: z.number(),
    meanElevation: z.number(),
    area2d: z.number(),
    area3d: z.number(),
    numberOfPoints: z.number(),
    numberOfTriangles: z.number(),
  }),
  boundingBox: z.object({
    minX: z.number(),
    minY: z.number(),
    maxX: z.number(),
    maxY: z.number(),
  }),
  units: z.string(),
  isReference: z.boolean(),
  dependentAlignments: z.array(z.string()),
  dependentCorridors: z.array(z.string()),
});

const SurfaceElevationResponseSchema = z.object({
  elevation: z.number(),
  units: z.string(),
  surfaceName: z.string(),
});

const SurfaceVolumeResultSchema = z.object({
  baseSurface: z.string(),
  comparisonSurface: z.string(),
  cutVolume: z.number(),
  fillVolume: z.number(),
  netVolume: z.number(),
  cutArea: z.number().nullable(),
  fillArea: z.number().nullable(),
  method: z.string(),
  units: z.object({
    volume: z.string(),
    area: z.string(),
  }),
});

const SurfaceVolumeResponseSchema = z.object({
  cutVolume: z.number(),
  fillVolume: z.number(),
  netVolume: z.number(),
  cutArea: z.number().nullable(),
  fillArea: z.number().nullable(),
  units: z.object({
    volume: z.string(),
    area: z.string(),
  }),
});

const SurfaceStatisticsResultSchema = z.object({
  surfaceName: z.string(),
  minimumElevation: z.number(),
  maximumElevation: z.number(),
  meanElevation: z.number(),
  area2d: z.number(),
  area3d: z.number(),
  numberOfPoints: z.number(),
  numberOfTriangles: z.number(),
  units: z.object({
    horizontal: z.string(),
    vertical: z.string(),
    area: z.string(),
  }),
});

const FlowPathPointSchema = z.object({
  x: z.number(),
  y: z.number(),
  elevation: z.number(),
});

const SurfaceElevationAlongResponseSchema = z.object({
  surfaceName: z.string(),
  samples: z.array(FlowPathPointSchema),
  units: z.string(),
});

const HydrologyFlowPathResponseSchema = z.object({
  surfaceName: z.string(),
  status: z.enum(["complete", "stopped_flat", "stopped_local_minimum", "max_steps_reached"]),
  stepDistance: z.number(),
  stepCount: z.number(),
  totalDistance: z.number(),
  dropElevation: z.number(),
  startPoint: FlowPathPointSchema,
  endPoint: FlowPathPointSchema,
  points: z.array(FlowPathPointSchema),
  units: z.object({
    horizontal: z.string(),
    vertical: z.string(),
  }),
});

const HydrologyRunoffResponseSchema = z.object({
  method: z.literal("rational"),
  drainageArea: z.number(),
  runoffCoefficient: z.number(),
  rainfallIntensity: z.number(),
  areaUnits: z.enum(["acres", "hectares"]),
  intensityUnits: z.enum(["in_per_hr", "mm_per_hr"]),
  runoffRate: z.object({
    cfs: z.number(),
    cubicMetersPerSecond: z.number(),
  }),
  normalizedInputs: z.object({
    drainageAreaAcres: z.number(),
    drainageAreaHectares: z.number(),
    rainfallIntensityInPerHr: z.number(),
    rainfallIntensityMmPerHr: z.number(),
  }),
});

const DrainageElevationProfileSchema = z.object({
  surfaceName: z.string(),
  sampleCount: z.number(),
  startElevation: z.number(),
  endElevation: z.number(),
  minimumElevation: z.number(),
  maximumElevation: z.number(),
  totalDrop: z.number(),
  units: z.string(),
  samples: z.array(FlowPathPointSchema),
});

const SurfaceComparisonWorkflowResponseSchema = z.object({
  baseSurface: SurfaceDetailResponseSchema,
  comparisonSurface: SurfaceDetailResponseSchema,
  volumeAnalysis: SurfaceVolumeResponseSchema,
  summary: z.object({
    baseSurfaceName: z.string(),
    comparisonSurfaceName: z.string(),
    elevationDeltaMean: z.number(),
    elevationDeltaMin: z.number(),
    elevationDeltaMax: z.number(),
    area2dDelta: z.number(),
    area3dDelta: z.number(),
    pointCountDelta: z.number(),
    triangleCountDelta: z.number(),
    netVolumeDirection: z.enum(["cut", "fill", "balanced"]),
  }),
});

const SurfaceDrainageWorkflowResponseSchema = z.object({
  surface: SurfaceDetailResponseSchema,
  flowPath: HydrologyFlowPathResponseSchema,
  elevationProfile: DrainageElevationProfileSchema,
  runoffEstimate: HydrologyRunoffResponseSchema,
});

const canonicalSurfaceInputShape = {
  action: z.enum([
    "list",
    "get",
    "get_elevation",
    "get_elevation_along",
    "get_statistics",
    "create",
    "delete",
    "add_points",
    "add_breakline",
    "add_boundary",
    "extract_contours",
    "compute_volume",
    "volume_calculate",
    "volume_report",
    "volume_by_region",
    "analyze_slope",
    "analyze_elevation",
    "analyze_directions",
    "watershed_add",
    "contour_interval_set",
    "statistics_get",
    "sample_elevations",
    "create_from_dem",
    "comparison_workflow",
    "drainage_workflow",
    "create_ex",
    "create_from_source",
    "add_data",
    "definition",
    "edit",
    "manage",
    "extract",
    "bounded_volume",
    "export_dem",
    "mask",
    "analysis_set",
    "point_file_formats",
  ]),
  name: z.string().optional(),
  surfaceType: z.enum(["tin", "grid", "tin_volume", "grid_volume"]).optional().describe("create_ex."),
  source: z.enum(["landxml", "tin", "corridor_surface", "cropping"]).optional().describe("create_from_source."),
  kind: z.enum(["points", "point_group", "point_file", "dem_file", "drawing_objects", "breaklines", "breakline_file", "boundary", "contours"]).optional().describe("add_data: what to add to the surface definition."),
  operation: z.string().optional().describe("definition / edit / manage / mask: the sub-operation (see each action's enum)."),
  what: z.enum(["border", "watershed", "gridded", "major_contours", "minor_contours", "contour_at", "contours", "water_drop", "solids"]).optional().describe("extract."),
  analysis: z.enum(["elevation", "slope", "slope_arrow", "direction"]).optional().describe("analysis_set."),
  handles: z.array(z.string()).optional(),
  handle: z.string().optional(),
  objectType: z.string().optional(),
  polylineHandle: z.string().optional(),
  pointGroupName: z.string().optional(),
  pointFileFormat: z.string().optional(),
  coordinateSystemCode: z.string().optional(),
  customNullElevation: z.number().optional(),
  maintainEdges: z.boolean().optional(),
  nonDestructiveBreakline: z.boolean().optional(),
  midOrdinateDistance: z.number().positive().optional(),
  maximumDistance: z.number().nonnegative().optional(),
  weedingDistance: z.number().nonnegative().optional(),
  weedingAngleDegrees: z.number().nonnegative().optional(),
  fillGaps: z.boolean().optional(), swapEdges: z.boolean().optional(), addPointsToTriangles: z.boolean().optional(), addPointsToEdges: z.boolean().optional(),
  index: z.number().int().nonnegative().optional(),
  operationType: z.string().optional(),
  deltaElevation: z.number().optional(),
  elevation: z.number().optional(),
  pasteSurface: z.string().optional(),
  z: z.number().optional(), newX: z.number().optional(), newY: z.number().optional(),
  x1: z.number().optional(), y1: z.number().optional(), x2: z.number().optional(), y2: z.number().optional(),
  tolerance: z.number().positive().optional(),
  simplifyMethod: z.enum(["point_removal", "edge_contraction"]).optional(),
  percentToRemove: z.number().positive().max(100).optional(),
  maxElevationChange: z.number().positive().optional(),
  smoothMethod: z.enum(["nni", "kriging"]).optional(),
  outputLocations: z.enum(["grid", "centroids", "random", "edge_midpoints"]).optional(),
  gridSpacingX: z.number().positive().optional(), gridSpacingY: z.number().positive().optional(), gridOrientationDegrees: z.number().optional(),
  randomPoints: z.number().int().positive().optional(),
  semivariogram: z.enum(["spherical", "exponential", "linear", "gaussian", "monomial"]).optional(),
  variogramA: z.number().optional(), variogramC: z.number().optional(), nuggetEffect: z.number().optional(),
  value: z.boolean().optional(),
  newName: z.string().optional(),
  maximumTriangleLength: z.number().positive().optional(), useMaximumTriangleLength: z.boolean().optional(),
  excludeBelowElevation: z.number().optional(), useExcludeBelowElevation: z.boolean().optional(),
  excludeAboveElevation: z.number().optional(), useExcludeAboveElevation: z.boolean().optional(),
  maximumAngleDegrees: z.number().positive().optional(), useMaximumAngle: z.boolean().optional(),
  crossingBreaklines: z.enum(["first", "last", "average", "none"]).optional(),
  copyDeletedDependentObjects: z.boolean().optional(), convertProximityBreaklines: z.boolean().optional(),
  settings: z.enum(["plan", "model"]).optional(),
  smoothing: z.enum(["none", "add_vertices", "spline"]).optional(),
  smoothFactor: z.number().int().nonnegative().optional(),
  interval: z.number().positive().optional(),
  lowElevation: z.number().optional(), highElevation: z.number().optional(),
  solidMode: z.enum(["depth", "elevation", "surface"]).optional(),
  depth: z.number().positive().optional(),
  bottomSurface: z.string().optional(),
  colorIndex: z.number().int().min(1).max(256).optional(),
  maxListed: z.number().int().positive().optional(),
  datumElevation: z.number().optional(),
  outputPath: z.string().optional(),
  elevationMethod: z.enum(["sample", "average"]).optional(),
  maskName: z.string().optional(),
  maskType: z.enum(["outside", "inside"]).optional(),
  renderOnly: z.boolean().optional(),
  rangeCount: z.number().int().min(1).max(64).optional(),
  firstColorIndex: z.number().int().min(1).max(255).optional(),
  colorStep: z.number().int().optional(),
  spacingX: z.number().positive().optional(), spacingY: z.number().positive().optional(), orientationDegrees: z.number().optional(),
  cutFactor: z.number().positive().optional(), fillFactor: z.number().positive().optional(),
  landXmlSurfaceName: z.string().optional(),
  corridorName: z.string().optional(),
  corridorSurfaceName: z.string().optional(),
  sourceSurface: z.string().optional(),
  rebuild: z.boolean().optional(),
  x: z.number().optional(),
  y: z.number().optional(),
  points: z.array(z.union([Point2DOr3DSchema, z.array(z.number()).min(2).max(3)])).optional(),
  analysisType: z.enum(["elevations", "slopes", "contours", "watersheds"]).optional(),
  style: z.string().optional(),
  layer: z.string().optional(),
  description: z.string().optional(),
  breaklineType: z.enum(["standard", "wall", "proximity", "non_destructive"]).optional(),
  boundaryType: z.enum(["show", "hide", "outer", "data_clip"]).optional(),
  minorInterval: z.number().optional(),
  majorInterval: z.number().optional(),
  baseSurface: z.string().optional(),
  comparisonSurface: z.string().optional(),
  method: z.enum(["tin_volume", "average_end_area", "prismoidal"]).optional(),
  format: z.enum(["summary", "detailed"]).optional(),
  boundary: z.array(Point2DSchema).optional(),
  ranges: z.array(z.object({ min: z.number(), max: z.number(), colorIndex: z.number().int().optional() })).optional(),
  numRanges: z.number().int().optional(),
  depthThreshold: z.number().positive().optional(),
  mergeAdjacentWatersheds: z.boolean().optional(),
  gridSpacing: z.number().positive().optional(),
  startPoint: Point2DSchema.optional(),
  endPoint: Point2DSchema.optional(),
  numSamples: z.number().int().min(2).optional(),
  filePath: z.string().optional(),
  coordinateSystem: z.string().optional(),
  stepDistance: z.number().positive().optional(),
  maxSteps: z.number().int().positive().optional(),
  drainageArea: z.number().positive().optional(),
  runoffCoefficient: z.number().min(0).max(1).optional(),
  rainfallIntensity: z.number().positive().optional(),
  areaUnits: z.enum(["acres", "hectares"]).optional(),
  intensityUnits: z.enum(["in_per_hr", "mm_per_hr"]).optional(),
};

const SurfaceListArgsSchema = z.object({
  action: z.literal("list"),
});

const SurfaceGetArgsSchema = z.object({
  action: z.literal("get"),
  name: z.string(),
});

const SurfaceGetElevationArgsSchema = z.object({
  action: z.literal("get_elevation"),
  name: z.string(),
  x: z.number(),
  y: z.number(),
});

const SurfaceGetElevationAlongArgsSchema = z.object({
  action: z.literal("get_elevation_along"),
  name: z.string(),
  points: z.array(Point2DSchema).min(1),
});

const SurfaceGetStatisticsArgsSchema = z.object({
  action: z.literal("get_statistics"),
  name: z.string(),
  analysisType: z.enum(["elevations", "slopes", "contours", "watersheds"]).optional(),
});

const SurfaceCreateArgsSchema = z.object({
  action: z.literal("create"),
  name: z.string(),
  style: z.string().optional(),
  layer: z.string().optional(),
  description: z.string().optional(),
});

const SurfaceDeleteArgsSchema = z.object({
  action: z.literal("delete"),
  name: z.string(),
});

const SurfaceAddPointsArgsSchema = z.object({
  action: z.literal("add_points"),
  name: z.string(),
  points: z.array(Point3DSchema),
  description: z.string().optional(),
});

const SurfaceAddBreaklineArgsSchema = z.object({
  action: z.literal("add_breakline"),
  name: z.string(),
  breaklineType: z.enum(["standard", "wall", "proximity"]),
  points: z.array(Point3DSchema),
  description: z.string().optional(),
});

const SurfaceAddBoundaryArgsSchema = z.object({
  action: z.literal("add_boundary"),
  name: z.string(),
  boundaryType: z.enum(["show", "hide", "outer", "data_clip"]),
  points: z.array(Point2DSchema),
});

const SurfaceExtractContoursArgsSchema = z.object({
  action: z.literal("extract_contours"),
  name: z.string(),
  minorInterval: z.number(),
  majorInterval: z.number(),
});

const SurfaceComputeVolumeArgsSchema = z.object({
  action: z.literal("compute_volume"),
  baseSurface: z.string(),
  comparisonSurface: z.string(),
});

const SurfaceVolumeCalculateArgsSchema = z.object({
  action: z.literal("volume_calculate"),
  baseSurface: z.string(),
  comparisonSurface: z.string(),
  method: z.enum(["tin_volume", "average_end_area", "prismoidal"]).optional(),
});

const SurfaceVolumeReportArgsSchema = z.object({
  action: z.literal("volume_report"),
  baseSurface: z.string(),
  comparisonSurface: z.string(),
  format: z.enum(["summary", "detailed"]).optional(),
});

const SurfaceVolumeByRegionArgsSchema = z.object({
  action: z.literal("volume_by_region"),
  baseSurface: z.string(),
  comparisonSurface: z.string(),
  boundary: z.array(Point2DSchema).min(3),
});

const SurfaceAnalyzeSlopeArgsSchema = z.object({
  action: z.literal("analyze_slope"),
  name: z.string(),
  ranges: z.array(z.object({ min: z.number(), max: z.number() })).optional(),
  numRanges: z.number().int().min(2).max(20).optional(),
});

const SurfaceAnalyzeElevationArgsSchema = z.object({
  action: z.literal("analyze_elevation"),
  name: z.string(),
  ranges: z.array(z.object({ min: z.number(), max: z.number() })).optional(),
  numRanges: z.number().int().min(2).max(20).optional(),
});

const SurfaceAnalyzeDirectionsArgsSchema = z.object({
  action: z.literal("analyze_directions"),
  name: z.string(),
  numRanges: z.number().int().min(4).max(16).optional(),
});

const SurfaceWatershedAddArgsSchema = z.object({
  action: z.literal("watershed_add"),
  name: z.string(),
  depthThreshold: z.number().positive().optional(),
  mergeAdjacentWatersheds: z.boolean().optional(),
});

const SurfaceContourIntervalSetArgsSchema = z.object({
  action: z.literal("contour_interval_set"),
  name: z.string(),
  minorInterval: z.number().positive(),
  majorInterval: z.number().positive(),
});

const SurfaceStatisticsGetArgsSchema = z.object({
  action: z.literal("statistics_get"),
  name: z.string(),
});

const SurfaceSampleElevationsArgsSchema = z.object({
  action: z.literal("sample_elevations"),
  name: z.string(),
  method: z.enum(["grid", "points", "transect"]),
  gridSpacing: z.number().positive().optional(),
  points: z.array(Point2DSchema).optional(),
  startPoint: Point2DSchema.optional(),
  endPoint: Point2DSchema.optional(),
  numSamples: z.number().int().min(2).optional(),
  boundary: z.array(Point2DSchema).optional(),
});

const SurfaceCreateFromDemArgsSchema = z.object({
  action: z.literal("create_from_dem"),
  filePath: z.string(),
  name: z.string(),
  style: z.string().optional(),
  layer: z.string().optional(),
  description: z.string().optional(),
  coordinateSystem: z.string().optional(),
});

// ─── Surface extras (SurfaceExtraCommands.cs) ────────────────────────────────

const PointLooseSchema = z.union([Point2DOr3DSchema, z.array(z.number()).min(2).max(3)]);
const SurfaceEntitySelectionShape = {
  handles: z.array(z.string()).optional().describe("AutoCAD handles of drawing objects."),
  handle: z.string().optional(),
  layer: z.string().optional().describe("Select every model-space entity on this layer (optionally filtered by objectType)."),
  objectType: z.string().optional().describe("Type-name filter with layer, e.g. Polyline, Polyline3d, DBPoint, BlockReference, Face."),
};

const SurfaceCreateExArgsSchema = z.object({
  action: z.literal("create_ex"),
  name: z.string(),
  surfaceType: z.enum(["tin", "grid", "tin_volume", "grid_volume"]).optional().describe("Default tin."),
  style: z.string().optional(),
  layer: z.string().optional(),
  description: z.string().optional(),
  baseSurface: z.string().optional().describe("Volume surfaces: base (existing ground)."),
  comparisonSurface: z.string().optional().describe("Volume surfaces: comparison (design)."),
  spacingX: z.number().positive().optional().describe("Grid surfaces (default 10)."),
  spacingY: z.number().positive().optional(),
  orientationDegrees: z.number().optional(),
  cutFactor: z.number().positive().optional(),
  fillFactor: z.number().positive().optional(),
});

const SurfaceCreateFromSourceArgsSchema = z.object({
  action: z.literal("create_from_source"),
  source: z.enum(["landxml", "tin", "corridor_surface", "cropping"]),
  name: z.string().optional().describe("New surface name (default derived from the source)."),
  filePath: z.string().optional().describe("landxml / tin: file on the Civil 3D machine."),
  landXmlSurfaceName: z.string().optional().describe("landxml: which surface in the file (default first)."),
  corridorName: z.string().optional(),
  corridorSurfaceName: z.string().optional(),
  sourceSurface: z.string().optional().describe("cropping: surface to crop."),
  points: z.array(PointLooseSchema).optional().describe("cropping: polygon."),
  polylineHandle: z.string().optional().describe("cropping: closed polyline."),
  style: z.string().optional(),
  layer: z.string().optional(),
});

const SurfaceAddDataArgsSchema = z.object({
  action: z.literal("add_data"),
  name: z.string(),
  kind: z.enum(["points", "point_group", "point_file", "dem_file", "drawing_objects", "breaklines", "breakline_file", "boundary", "contours"]),
  ...SurfaceEntitySelectionShape,
  points: z.array(PointLooseSchema).optional().describe("points / breaklines / boundary / contours given as coordinates."),
  pointGroupName: z.string().optional(),
  filePath: z.string().optional(),
  pointFileFormat: z.string().optional().describe("point_file: format name (see point_file_formats), e.g. PNEZD (comma delimited)."),
  coordinateSystemCode: z.string().optional().describe("dem_file: source coordinate system code."),
  customNullElevation: z.number().optional(),
  maintainEdges: z.boolean().optional().describe("drawing_objects: keep lines / face edges as breaklines."),
  breaklineType: z.enum(["standard", "proximity", "non_destructive"]).optional(),
  boundaryType: z.enum(["outer", "show", "hide", "data_clip"]).optional(),
  nonDestructiveBreakline: z.boolean().optional(),
  midOrdinateDistance: z.number().positive().optional().describe("Curve tessellation (default 1)."),
  maximumDistance: z.number().nonnegative().optional().describe("Supplementing distance (0 = off)."),
  weedingDistance: z.number().nonnegative().optional(),
  weedingAngleDegrees: z.number().nonnegative().optional(),
  fillGaps: z.boolean().optional(),
  swapEdges: z.boolean().optional(),
  addPointsToTriangles: z.boolean().optional(),
  addPointsToEdges: z.boolean().optional(),
  description: z.string().optional(),
  rebuild: z.boolean().optional(),
});

const SurfaceDefinitionArgsSchema = z.object({
  action: z.literal("definition"),
  name: z.string(),
  operation: z.enum(["list", "enable", "disable", "remove", "move_up", "move_down", "move_top", "move_bottom", "enable_type", "disable_type"]).optional().describe("Default list."),
  index: z.number().int().nonnegative().optional().describe("Operation index from list."),
  operationType: z.string().optional().describe("enable_type / disable_type: e.g. AddBreakline, AddBoundary, AddPointGroup, AddDrawingObject, AddContour, Raise, PasteSurface."),
  rebuild: z.boolean().optional(),
});

const SurfaceEditExArgsSchema = z.object({
  action: z.literal("edit"),
  name: z.string(),
  operation: z.enum([
    "raise", "lower", "paste", "add_point", "delete_point", "modify_point", "move_point",
    "add_line", "delete_line", "swap_edge",
    "delete_points_in_polygon", "raise_points_in_polygon", "set_points_elevation_in_polygon",
    "minimize_flat_areas", "simplify", "smooth",
  ]),
  deltaElevation: z.number().optional(),
  elevation: z.number().optional(),
  pasteSurface: z.string().optional(),
  x: z.number().optional(), y: z.number().optional(), z: z.number().optional(),
  newX: z.number().optional(), newY: z.number().optional(),
  x1: z.number().optional(), y1: z.number().optional(), x2: z.number().optional(), y2: z.number().optional(),
  tolerance: z.number().positive().optional().describe("Vertex pick tolerance (default 0.5)."),
  points: z.array(PointLooseSchema).optional().describe("add_point: several points; *_in_polygon / simplify / smooth: region polygon."),
  polylineHandle: z.string().optional().describe("Region as a closed polyline."),
  fillGaps: z.boolean().optional(), swapEdges: z.boolean().optional(), addPointsToTriangles: z.boolean().optional(), addPointsToEdges: z.boolean().optional(),
  simplifyMethod: z.enum(["point_removal", "edge_contraction"]).optional(),
  percentToRemove: z.number().positive().max(100).optional(),
  maxElevationChange: z.number().positive().optional(),
  smoothMethod: z.enum(["nni", "kriging"]).optional(),
  outputLocations: z.enum(["grid", "centroids", "random", "edge_midpoints"]).optional(),
  gridSpacing: z.number().positive().optional(),
  gridSpacingX: z.number().positive().optional(),
  gridSpacingY: z.number().positive().optional(),
  gridOrientationDegrees: z.number().optional(),
  randomPoints: z.number().int().positive().optional(),
  semivariogram: z.enum(["spherical", "exponential", "linear", "gaussian", "monomial"]).optional(),
  variogramA: z.number().optional(), variogramC: z.number().optional(), nuggetEffect: z.number().optional(),
  rebuild: z.boolean().optional(),
});

const SurfaceManageArgsSchema = z.object({
  action: z.literal("manage"),
  name: z.string(),
  operation: z.enum(["rebuild", "snapshot_create", "snapshot_rebuild", "snapshot_remove", "set_auto_rebuild", "set_lock", "rename", "set_style", "set_description", "set_layer", "build_options"]),
  value: z.boolean().optional().describe("set_auto_rebuild / set_lock."),
  newName: z.string().optional(),
  style: z.string().optional(),
  description: z.string().optional(),
  layer: z.string().optional(),
  maximumTriangleLength: z.number().positive().optional(),
  useMaximumTriangleLength: z.boolean().optional(),
  excludeBelowElevation: z.number().optional(),
  useExcludeBelowElevation: z.boolean().optional(),
  excludeAboveElevation: z.number().optional(),
  useExcludeAboveElevation: z.boolean().optional(),
  maximumAngleDegrees: z.number().positive().optional(),
  useMaximumAngle: z.boolean().optional(),
  crossingBreaklines: z.enum(["first", "last", "average", "none"]).optional(),
  copyDeletedDependentObjects: z.boolean().optional(),
  convertProximityBreaklines: z.boolean().optional(),
  rebuild: z.boolean().optional(),
});

const SurfaceExtractArgsSchema = z.object({
  action: z.literal("extract"),
  name: z.string(),
  what: z.enum(["border", "watershed", "gridded", "major_contours", "minor_contours", "contour_at", "contours", "water_drop", "solids"]),
  settings: z.enum(["plan", "model"]).optional().describe("Which display settings to extract from (default plan)."),
  smoothing: z.enum(["none", "add_vertices", "spline"]).optional(),
  smoothFactor: z.number().int().nonnegative().optional(),
  elevation: z.number().optional().describe("contour_at / solids (elevation mode)."),
  interval: z.number().positive().optional().describe("contours."),
  lowElevation: z.number().optional(),
  highElevation: z.number().optional(),
  x: z.number().optional().describe("water_drop start."),
  y: z.number().optional(),
  objectType: z.enum(["polyline2d", "polyline3d"]).optional().describe("water_drop (default polyline3d)."),
  solidMode: z.enum(["depth", "elevation", "surface"]).optional(),
  depth: z.number().positive().optional(),
  bottomSurface: z.string().optional(),
  colorIndex: z.number().int().min(1).max(256).optional(),
  layer: z.string().optional().describe("Layer for the extracted objects."),
  maxListed: z.number().int().positive().optional(),
});

const SurfaceBoundedVolumeArgsSchema = z.object({
  action: z.literal("bounded_volume"),
  name: z.string(),
  points: z.array(PointLooseSchema).optional(),
  polylineHandle: z.string().optional(),
  datumElevation: z.number().optional(),
});

const SurfaceExportDemArgsSchema = z.object({
  action: z.literal("export_dem"),
  name: z.string(),
  outputPath: z.string(),
  gridSpacing: z.number().positive(),
  coordinateSystemCode: z.string().optional(),
  elevationMethod: z.enum(["sample", "average"]).optional(),
  customNullElevation: z.number().optional(),
});

const SurfaceMaskArgsSchema = z.object({
  action: z.literal("mask"),
  name: z.string(),
  operation: z.enum(["list", "add", "remove"]).optional(),
  maskName: z.string().optional(),
  ...SurfaceEntitySelectionShape,
  maskType: z.enum(["outside", "inside"]).optional(),
  midOrdinateDistance: z.number().positive().optional(),
  renderOnly: z.boolean().optional(),
  description: z.string().optional(),
});

const SurfaceAnalysisSetArgsSchema = z.object({
  action: z.literal("analysis_set"),
  name: z.string(),
  analysis: z.enum(["elevation", "slope", "slope_arrow", "direction"]),
  rangeCount: z.number().int().min(1).max(64).optional().describe("Equal ranges between the surface min/max (default 8) when ranges is omitted."),
  ranges: z.array(z.object({ min: z.number(), max: z.number(), colorIndex: z.number().int().min(1).max(255).optional() })).optional(),
  firstColorIndex: z.number().int().min(1).max(255).optional(),
  colorStep: z.number().int().optional(),
});

const SurfacePointFileFormatsArgsSchema = z.object({
  action: z.literal("point_file_formats"),
});

const SurfaceComparisonWorkflowArgsSchema = z.object({
  action: z.literal("comparison_workflow"),
  baseSurface: z.string(),
  comparisonSurface: z.string(),
});

const SurfaceDrainageWorkflowArgsSchema = z.object({
  action: z.literal("drainage_workflow"),
  name: z.string(),
  x: z.number(),
  y: z.number(),
  stepDistance: z.number().positive().optional(),
  maxSteps: z.number().int().positive().optional(),
  drainageArea: z.number().positive(),
  runoffCoefficient: z.number().min(0).max(1),
  rainfallIntensity: z.number().positive(),
  areaUnits: z.enum(["acres", "hectares"]),
  intensityUnits: z.enum(["in_per_hr", "mm_per_hr"]),
});

export const SURFACE_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "surface",
  actions: {
    list: {
      action: "list",
      inputSchema: SurfaceListArgsSchema,
      responseSchema: SurfaceListResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listSurfaces"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listSurfaces", {}),
      ),
    },
    get: {
      action: "get",
      inputSchema: SurfaceGetArgsSchema,
      responseSchema: SurfaceDetailResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSurface"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getSurface", {
          name: args.name,
        }),
      ),
    },
    get_elevation: {
      action: "get_elevation",
      inputSchema: SurfaceGetElevationArgsSchema,
      responseSchema: SurfaceElevationResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSurfaceElevation"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getSurfaceElevation", {
          name: args.name,
          x: args.x,
          y: args.y,
        }),
      ),
    },
    get_elevation_along: {
      action: "get_elevation_along",
      inputSchema: SurfaceGetElevationAlongArgsSchema,
      responseSchema: SurfaceElevationAlongResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSurfaceElevationsAlong"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getSurfaceElevationsAlong", {
          name: args.name,
          points: args.points,
        }),
      ),
    },
    get_statistics: {
      action: "get_statistics",
      inputSchema: SurfaceGetStatisticsArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSurfaceStatistics"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getSurfaceStatistics", {
          name: args.name,
          analysisType: args.analysisType,
        }),
      ),
    },
    create: {
      action: "create",
      inputSchema: SurfaceCreateArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSurface"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createSurface", {
          name: args.name,
          style: args.style,
          layer: args.layer,
          description: args.description,
        }),
      ),
    },
    delete: {
      action: "delete",
      inputSchema: SurfaceDeleteArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["delete"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["deleteSurface"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("deleteSurface", {
          name: args.name,
        }),
      ),
    },
    add_points: {
      action: "add_points",
      inputSchema: SurfaceAddPointsArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addSurfacePoints"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("addSurfacePoints", {
          name: args.name,
          points: args.points,
          description: args.description,
        }),
      ),
    },
    add_breakline: {
      action: "add_breakline",
      inputSchema: SurfaceAddBreaklineArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addSurfaceBreakline"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("addSurfaceBreakline", {
          name: args.name,
          breaklineType: args.breaklineType,
          points: args.points,
          description: args.description,
        }),
      ),
    },
    add_boundary: {
      action: "add_boundary",
      inputSchema: SurfaceAddBoundaryArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addSurfaceBoundary"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("addSurfaceBoundary", {
          name: args.name,
          boundaryType: args.boundaryType,
          points: args.points,
        }),
      ),
    },
    extract_contours: {
      action: "extract_contours",
      inputSchema: SurfaceExtractContoursArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["generate"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["extractSurfaceContours"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("extractSurfaceContours", {
          name: args.name,
          minorInterval: args.minorInterval,
          majorInterval: args.majorInterval,
        }),
      ),
    },
    compute_volume: {
      action: "compute_volume",
      inputSchema: SurfaceComputeVolumeArgsSchema,
      responseSchema: SurfaceVolumeResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["computeSurfaceVolume"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("computeSurfaceVolume", {
          baseSurface: args.baseSurface,
          comparisonSurface: args.comparisonSurface,
        }),
      ),
    },
    volume_calculate: {
      action: "volume_calculate",
      inputSchema: SurfaceVolumeCalculateArgsSchema,
      responseSchema: SurfaceVolumeResultSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["calculateSurfaceVolume"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("calculateSurfaceVolume", {
          baseSurface: args.baseSurface,
          comparisonSurface: args.comparisonSurface,
          method: args.method ?? "tin_volume",
        }),
      ),
    },
    volume_report: {
      action: "volume_report",
      inputSchema: SurfaceVolumeReportArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["query", "analyze", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSurfaceVolumeReport"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getSurfaceVolumeReport", {
          baseSurface: args.baseSurface,
          comparisonSurface: args.comparisonSurface,
          format: args.format ?? "summary",
        }),
      ),
    },
    volume_by_region: {
      action: "volume_by_region",
      inputSchema: SurfaceVolumeByRegionArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["calculateSurfaceVolumeByRegion"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("calculateSurfaceVolumeByRegion", {
          baseSurface: args.baseSurface,
          comparisonSurface: args.comparisonSurface,
          boundary: args.boundary,
        }),
      ),
    },
    analyze_slope: {
      action: "analyze_slope",
      inputSchema: SurfaceAnalyzeSlopeArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["analyzeSurfaceSlope"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("analyzeSurfaceSlope", {
          name: args.name,
          ranges: args.ranges ?? null,
          numRanges: args.numRanges ?? 5,
        }),
      ),
    },
    analyze_elevation: {
      action: "analyze_elevation",
      inputSchema: SurfaceAnalyzeElevationArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["analyzeSurfaceElevation"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("analyzeSurfaceElevation", {
          name: args.name,
          ranges: args.ranges ?? null,
          numRanges: args.numRanges ?? 5,
        }),
      ),
    },
    analyze_directions: {
      action: "analyze_directions",
      inputSchema: SurfaceAnalyzeDirectionsArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["analyzeSurfaceDirections"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("analyzeSurfaceDirections", {
          name: args.name,
          numRanges: args.numRanges ?? 8,
        }),
      ),
    },
    watershed_add: {
      action: "watershed_add",
      inputSchema: SurfaceWatershedAddArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["edit", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addSurfaceWatershed"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("addSurfaceWatershed", {
          name: args.name,
          depthThreshold: args.depthThreshold ?? 0.1,
          mergeAdjacentWatersheds: args.mergeAdjacentWatersheds ?? false,
        }),
      ),
    },
    contour_interval_set: {
      action: "contour_interval_set",
      inputSchema: SurfaceContourIntervalSetArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["setSurfaceContourInterval"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("setSurfaceContourInterval", {
          name: args.name,
          minorInterval: args.minorInterval,
          majorInterval: args.majorInterval,
        }),
      ),
    },
    statistics_get: {
      action: "statistics_get",
      inputSchema: SurfaceStatisticsGetArgsSchema,
      responseSchema: SurfaceStatisticsResultSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSurfaceStatisticsDetailed"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getSurfaceStatisticsDetailed", {
          name: args.name,
        }),
      ),
    },
    sample_elevations: {
      action: "sample_elevations",
      inputSchema: SurfaceSampleElevationsArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["sampleSurfaceElevations"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("sampleSurfaceElevations", {
          name: args.name,
          method: args.method,
          gridSpacing: args.gridSpacing,
          points: args.points,
          startPoint: args.startPoint,
          endPoint: args.endPoint,
          numSamples: args.numSamples,
          boundary: args.boundary,
        }),
      ),
    },
    create_from_dem: {
      action: "create_from_dem",
      inputSchema: SurfaceCreateFromDemArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["create", "import"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSurfaceFromDem"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createSurfaceFromDem", {
          filePath: args.filePath,
          name: args.name,
          style: args.style,
          layer: args.layer,
          description: args.description,
          coordinateSystem: args.coordinateSystem,
        }),
      ),
    },
    create_ex: {
      action: "create_ex",
      inputSchema: SurfaceCreateExArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["surfaceCreateEx"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("surfaceCreateEx", {
          name: args.name,
          surfaceType: args.surfaceType ?? null,
          style: args.style ?? null,
          layer: args.layer ?? null,
          description: args.description ?? null,
          baseSurface: args.baseSurface ?? null,
          comparisonSurface: args.comparisonSurface ?? null,
          spacingX: args.spacingX ?? null,
          spacingY: args.spacingY ?? null,
          orientationDegrees: args.orientationDegrees ?? null,
          cutFactor: args.cutFactor ?? null,
          fillFactor: args.fillFactor ?? null,
        }),
      ),
    },
    create_from_source: {
      action: "create_from_source",
      inputSchema: SurfaceCreateFromSourceArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["create", "import"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["surfaceCreateFromSource"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("surfaceCreateFromSource", {
          source: args.source,
          name: args.name ?? null,
          filePath: args.filePath ?? null,
          landXmlSurfaceName: args.landXmlSurfaceName ?? null,
          corridorName: args.corridorName ?? null,
          corridorSurfaceName: args.corridorSurfaceName ?? null,
          sourceSurface: args.sourceSurface ?? null,
          points: args.points ?? null,
          polylineHandle: args.polylineHandle ?? null,
          style: args.style ?? null,
          layer: args.layer ?? null,
        }),
      ),
    },
    add_data: {
      action: "add_data",
      inputSchema: SurfaceAddDataArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["surfaceAddData"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("surfaceAddData", {
          name: args.name,
          kind: args.kind,
          handles: args.handles ?? null,
          handle: args.handle ?? null,
          layer: args.layer ?? null,
          objectType: args.objectType ?? null,
          points: args.points ?? null,
          pointGroupName: args.pointGroupName ?? null,
          filePath: args.filePath ?? null,
          pointFileFormat: args.pointFileFormat ?? null,
          coordinateSystemCode: args.coordinateSystemCode ?? null,
          customNullElevation: args.customNullElevation ?? null,
          maintainEdges: args.maintainEdges ?? null,
          breaklineType: args.breaklineType ?? null,
          boundaryType: args.boundaryType ?? null,
          nonDestructiveBreakline: args.nonDestructiveBreakline ?? null,
          midOrdinateDistance: args.midOrdinateDistance ?? null,
          maximumDistance: args.maximumDistance ?? null,
          weedingDistance: args.weedingDistance ?? null,
          weedingAngleDegrees: args.weedingAngleDegrees ?? null,
          fillGaps: args.fillGaps ?? null,
          swapEdges: args.swapEdges ?? null,
          addPointsToTriangles: args.addPointsToTriangles ?? null,
          addPointsToEdges: args.addPointsToEdges ?? null,
          description: args.description ?? null,
          rebuild: args.rebuild ?? true,
        }),
      ),
    },
    definition: {
      action: "definition",
      inputSchema: SurfaceDefinitionArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["query", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["surfaceDefinition"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("surfaceDefinition", {
          name: args.name,
          operation: args.operation ?? "list",
          index: args.index ?? null,
          operationType: args.operationType ?? null,
          rebuild: args.rebuild ?? true,
        }),
      ),
    },
    edit: {
      action: "edit",
      inputSchema: SurfaceEditExArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["surfaceEdit"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("surfaceEdit", {
          name: args.name,
          operation: args.operation,
          method: args.simplifyMethod ?? args.smoothMethod ?? null,
          deltaElevation: args.deltaElevation ?? null,
          elevation: args.elevation ?? null,
          pasteSurface: args.pasteSurface ?? null,
          x: args.x ?? null,
          y: args.y ?? null,
          z: args.z ?? null,
          newX: args.newX ?? null,
          newY: args.newY ?? null,
          x1: args.x1 ?? null,
          y1: args.y1 ?? null,
          x2: args.x2 ?? null,
          y2: args.y2 ?? null,
          tolerance: args.tolerance ?? null,
          points: args.points ?? null,
          polylineHandle: args.polylineHandle ?? null,
          fillGaps: args.fillGaps ?? null,
          swapEdges: args.swapEdges ?? null,
          addPointsToTriangles: args.addPointsToTriangles ?? null,
          addPointsToEdges: args.addPointsToEdges ?? null,
          percentToRemove: args.percentToRemove ?? null,
          maxElevationChange: args.maxElevationChange ?? null,
          outputLocations: args.outputLocations ?? null,
          gridSpacing: args.gridSpacing ?? null,
          gridSpacingX: args.gridSpacingX ?? null,
          gridSpacingY: args.gridSpacingY ?? null,
          gridOrientationDegrees: args.gridOrientationDegrees ?? null,
          randomPoints: args.randomPoints ?? null,
          semivariogram: args.semivariogram ?? null,
          variogramA: args.variogramA ?? null,
          variogramC: args.variogramC ?? null,
          nuggetEffect: args.nuggetEffect ?? null,
          rebuild: args.rebuild ?? true,
        }),
      ),
    },
    manage: {
      action: "manage",
      inputSchema: SurfaceManageArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["surfaceManage"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("surfaceManage", {
          name: args.name,
          operation: args.operation,
          value: args.value ?? null,
          newName: args.newName ?? null,
          style: args.style ?? null,
          description: args.description ?? null,
          layer: args.layer ?? null,
          maximumTriangleLength: args.maximumTriangleLength ?? null,
          useMaximumTriangleLength: args.useMaximumTriangleLength ?? null,
          excludeBelowElevation: args.excludeBelowElevation ?? null,
          useExcludeBelowElevation: args.useExcludeBelowElevation ?? null,
          excludeAboveElevation: args.excludeAboveElevation ?? null,
          useExcludeAboveElevation: args.useExcludeAboveElevation ?? null,
          maximumAngleDegrees: args.maximumAngleDegrees ?? null,
          useMaximumAngle: args.useMaximumAngle ?? null,
          crossingBreaklines: args.crossingBreaklines ?? null,
          copyDeletedDependentObjects: args.copyDeletedDependentObjects ?? null,
          convertProximityBreaklines: args.convertProximityBreaklines ?? null,
          rebuild: args.rebuild ?? null,
        }),
      ),
    },
    extract: {
      action: "extract",
      inputSchema: SurfaceExtractArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["create", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["surfaceExtract"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("surfaceExtract", {
          name: args.name,
          what: args.what,
          settings: args.settings ?? null,
          smoothing: args.smoothing ?? null,
          smoothFactor: args.smoothFactor ?? null,
          elevation: args.elevation ?? null,
          interval: args.interval ?? null,
          lowElevation: args.lowElevation ?? null,
          highElevation: args.highElevation ?? null,
          x: args.x ?? null,
          y: args.y ?? null,
          objectType: args.objectType ?? null,
          solidMode: args.solidMode ?? null,
          depth: args.depth ?? null,
          bottomSurface: args.bottomSurface ?? null,
          colorIndex: args.colorIndex ?? null,
          layer: args.layer ?? null,
          maxListed: args.maxListed ?? null,
        }),
      ),
    },
    bounded_volume: {
      action: "bounded_volume",
      inputSchema: SurfaceBoundedVolumeArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["surfaceBoundedVolume"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("surfaceBoundedVolume", {
          name: args.name,
          points: args.points ?? null,
          polylineHandle: args.polylineHandle ?? null,
          datumElevation: args.datumElevation ?? null,
        }),
      ),
    },
    export_dem: {
      action: "export_dem",
      inputSchema: SurfaceExportDemArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["export"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["surfaceExportDem"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("surfaceExportDem", {
          name: args.name,
          outputPath: args.outputPath,
          gridSpacing: args.gridSpacing,
          coordinateSystemCode: args.coordinateSystemCode ?? null,
          elevationMethod: args.elevationMethod ?? null,
          customNullElevation: args.customNullElevation ?? null,
        }),
      ),
    },
    mask: {
      action: "mask",
      inputSchema: SurfaceMaskArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["query", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["surfaceMask"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("surfaceMask", {
          name: args.name,
          operation: args.operation ?? "list",
          maskName: args.maskName ?? null,
          handles: args.handles ?? null,
          handle: args.handle ?? null,
          layer: args.layer ?? null,
          objectType: args.objectType ?? null,
          maskType: args.maskType ?? null,
          midOrdinateDistance: args.midOrdinateDistance ?? null,
          renderOnly: args.renderOnly ?? null,
          description: args.description ?? null,
        }),
      ),
    },
    analysis_set: {
      action: "analysis_set",
      inputSchema: SurfaceAnalysisSetArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["edit", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["surfaceAnalysisSet"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("surfaceAnalysisSet", {
          name: args.name,
          analysis: args.analysis,
          rangeCount: args.rangeCount ?? null,
          ranges: args.ranges ?? null,
          firstColorIndex: args.firstColorIndex ?? null,
          colorStep: args.colorStep ?? null,
        }),
      ),
    },
    point_file_formats: {
      action: "point_file_formats",
      inputSchema: SurfacePointFileFormatsArgsSchema,
      responseSchema: GenericSurfaceResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["surfacePointFileFormats"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("surfacePointFileFormats", {}),
      ),
    },
    comparison_workflow: {
      action: "comparison_workflow",
      inputSchema: SurfaceComparisonWorkflowArgsSchema,
      responseSchema: SurfaceComparisonWorkflowResponseSchema,
      capabilities: ["query", "analyze", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSurface", "computeSurfaceVolume"],
      execute: async (args) => await withApplicationConnection(async (appClient) => {
        const baseSurface = SurfaceDetailResponseSchema.parse(
          await appClient.sendCommand("getSurface", {
            name: args.baseSurface,
          }),
        );

        const comparisonSurface = SurfaceDetailResponseSchema.parse(
          await appClient.sendCommand("getSurface", {
            name: args.comparisonSurface,
          }),
        );

        const volumeAnalysis = SurfaceVolumeResponseSchema.parse(
          await appClient.sendCommand("computeSurfaceVolume", {
            baseSurface: args.baseSurface,
            comparisonSurface: args.comparisonSurface,
          }),
        );

        const netVolumeDirection = volumeAnalysis.netVolume > 0
          ? "fill"
          : volumeAnalysis.netVolume < 0
            ? "cut"
            : "balanced";

        return {
          baseSurface,
          comparisonSurface,
          volumeAnalysis,
          summary: {
            baseSurfaceName: baseSurface.name,
            comparisonSurfaceName: comparisonSurface.name,
            elevationDeltaMean: comparisonSurface.statistics.meanElevation - baseSurface.statistics.meanElevation,
            elevationDeltaMin: comparisonSurface.statistics.minimumElevation - baseSurface.statistics.minimumElevation,
            elevationDeltaMax: comparisonSurface.statistics.maximumElevation - baseSurface.statistics.maximumElevation,
            area2dDelta: comparisonSurface.statistics.area2d - baseSurface.statistics.area2d,
            area3dDelta: comparisonSurface.statistics.area3d - baseSurface.statistics.area3d,
            pointCountDelta: comparisonSurface.statistics.numberOfPoints - baseSurface.statistics.numberOfPoints,
            triangleCountDelta: comparisonSurface.statistics.numberOfTriangles - baseSurface.statistics.numberOfTriangles,
            netVolumeDirection,
          },
        };
      }),
    },
    drainage_workflow: {
      action: "drainage_workflow",
      inputSchema: SurfaceDrainageWorkflowArgsSchema,
      responseSchema: SurfaceDrainageWorkflowResponseSchema,
      capabilities: ["query", "analyze", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSurface", "traceHydrologyFlowPath", "getSurfaceElevationsAlong", "estimateHydrologyRunoff"],
      execute: async (args) => await withApplicationConnection(async (appClient) => {
        const surface = SurfaceDetailResponseSchema.parse(
          await appClient.sendCommand("getSurface", {
            name: args.name,
          }),
        );

        const flowPath = HydrologyFlowPathResponseSchema.parse(
          await appClient.sendCommand("traceHydrologyFlowPath", {
            surfaceName: args.name,
            x: args.x,
            y: args.y,
            stepDistance: args.stepDistance,
            maxSteps: args.maxSteps,
          }),
        );

        const sampledElevations = SurfaceElevationAlongResponseSchema.parse(
          await appClient.sendCommand("getSurfaceElevationsAlong", {
            name: args.name,
            points: flowPath.points.map((point) => ({
              x: point.x,
              y: point.y,
            })),
          }),
        );

        const runoffEstimate = HydrologyRunoffResponseSchema.parse(
          await appClient.sendCommand("estimateHydrologyRunoff", {
            drainageArea: args.drainageArea,
            runoffCoefficient: args.runoffCoefficient,
            rainfallIntensity: args.rainfallIntensity,
            areaUnits: args.areaUnits,
            intensityUnits: args.intensityUnits,
          }),
        );

        const elevations = sampledElevations.samples.map((sample) => sample.elevation);
        const startElevation = elevations[0] ?? 0;
        const endElevation = elevations[elevations.length - 1] ?? 0;

        return {
          surface,
          flowPath,
          elevationProfile: {
            surfaceName: sampledElevations.surfaceName,
            sampleCount: sampledElevations.samples.length,
            startElevation,
            endElevation,
            minimumElevation: Math.min(...elevations),
            maximumElevation: Math.max(...elevations),
            totalDrop: startElevation - endElevation,
            units: sampledElevations.units,
            samples: sampledElevations.samples,
          },
          runoffEstimate,
        };
      }),
    },
  },
  exposures: [
    {
      toolName: "civil3d_surface",
      displayName: "Civil 3D Surface",
      description: "Reads, creates (TIN / grid / volume, LandXML, TIN file, corridor surface, cropping), defines (points, point groups/files, DEM, drawing objects, breaklines, boundaries, contours), edits (raise, paste, points/lines/edges, minimize flat areas, simplify, smooth), manages (rebuild, snapshot, build options, masks, analysis ranges), extracts (border, contours, watershed, water drop, solids, DEM export) and analyzes Civil 3D surfaces through a single domain tool.",
      inputShape: canonicalSurfaceInputShape,
      supportedActions: [
        "list",
        "get",
        "get_elevation",
        "get_elevation_along",
        "get_statistics",
        "create",
        "delete",
        "add_points",
        "add_breakline",
        "add_boundary",
        "extract_contours",
        "compute_volume",
        "volume_calculate",
        "volume_report",
        "volume_by_region",
        "analyze_slope",
        "analyze_elevation",
        "analyze_directions",
        "watershed_add",
        "contour_interval_set",
        "statistics_get",
        "sample_elevations",
        "create_from_dem",
        "comparison_workflow",
        "drainage_workflow",
        "create_ex",
        "create_from_source",
        "add_data",
        "definition",
        "edit",
        "manage",
        "extract",
        "bounded_volume",
        "export_dem",
        "mask",
        "analysis_set",
        "point_file_formats",
      ],
      resolveAction: (rawArgs) => ({
        action: String(rawArgs.action ?? ""),
        args: rawArgs,
      }),
    },
    {
      toolName: "civil3d_surface_edit",
      displayName: "Civil 3D Surface Edit",
      description: "Modifies Civil 3D surface data by adding points, breaklines, boundaries, extracting contours, and computing volumes.",
      inputShape: {
        action: z.enum(["add_points", "add_breakline", "add_boundary", "extract_contours", "compute_volume"]),
        name: z.string().optional(),
        points: z.array(Point2DOr3DSchema).optional(),
        description: z.string().optional(),
        breaklineType: z.enum(["standard", "wall", "proximity"]).optional(),
        boundaryType: z.enum(["show", "hide", "outer", "data_clip"]).optional(),
        minorInterval: z.number().optional(),
        majorInterval: z.number().optional(),
        baseSurface: z.string().optional(),
        comparisonSurface: z.string().optional(),
      },
      supportedActions: ["add_points", "add_breakline", "add_boundary", "extract_contours", "compute_volume"],
      resolveAction: (rawArgs) => ({
        action: String(rawArgs.action ?? ""),
        args: rawArgs,
      }),
    },
    {
      toolName: "civil3d_surface_volume_calculate",
      displayName: "Civil 3D Surface Volume Calculate",
      description: "Calculate cut/fill volumes between two Civil 3D surfaces.",
      inputShape: {
        baseSurface: z.string(),
        comparisonSurface: z.string(),
        method: z.enum(["tin_volume", "average_end_area", "prismoidal"]).optional(),
      },
      supportedActions: ["volume_calculate"],
      resolveAction: (rawArgs) => ({
        action: "volume_calculate",
        args: {
          action: "volume_calculate",
          baseSurface: rawArgs.baseSurface,
          comparisonSurface: rawArgs.comparisonSurface,
          method: rawArgs.method,
        },
      }),
    },
    {
      toolName: "civil3d_surface_volume_report",
      displayName: "Civil 3D Surface Volume Report",
      description: "Generate a formatted volume report comparing two Civil 3D surfaces.",
      inputShape: {
        baseSurface: z.string(),
        comparisonSurface: z.string(),
        format: z.enum(["summary", "detailed"]).optional(),
      },
      supportedActions: ["volume_report"],
      resolveAction: (rawArgs) => ({
        action: "volume_report",
        args: {
          action: "volume_report",
          baseSurface: rawArgs.baseSurface,
          comparisonSurface: rawArgs.comparisonSurface,
          format: rawArgs.format,
        },
      }),
    },
    {
      toolName: "civil3d_surface_volume_by_region",
      displayName: "Civil 3D Surface Volume By Region",
      description: "Calculate cut/fill volumes between two surfaces within a specific polygon region boundary.",
      inputShape: {
        baseSurface: z.string(),
        comparisonSurface: z.string(),
        boundary: z.array(Point2DSchema).min(3),
      },
      supportedActions: ["volume_by_region"],
      resolveAction: (rawArgs) => ({
        action: "volume_by_region",
        args: {
          action: "volume_by_region",
          baseSurface: rawArgs.baseSurface,
          comparisonSurface: rawArgs.comparisonSurface,
          boundary: rawArgs.boundary,
        },
      }),
    },
    {
      toolName: "civil3d_surface_analyze_slope",
      displayName: "Civil 3D Surface Analyze Slope",
      description: "Analyze slope distribution across a Civil 3D surface.",
      inputShape: {
        name: z.string(),
        ranges: z.array(z.object({ min: z.number(), max: z.number() })).optional(),
        numRanges: z.number().int().min(2).max(20).optional(),
      },
      supportedActions: ["analyze_slope"],
      resolveAction: (rawArgs) => ({
        action: "analyze_slope",
        args: {
          action: "analyze_slope",
          name: rawArgs.name,
          ranges: rawArgs.ranges,
          numRanges: rawArgs.numRanges,
        },
      }),
    },
    {
      toolName: "civil3d_surface_analyze_elevation",
      displayName: "Civil 3D Surface Analyze Elevation",
      description: "Analyze elevation band distribution across a Civil 3D surface.",
      inputShape: {
        name: z.string(),
        ranges: z.array(z.object({ min: z.number(), max: z.number() })).optional(),
        numRanges: z.number().int().min(2).max(20).optional(),
      },
      supportedActions: ["analyze_elevation"],
      resolveAction: (rawArgs) => ({
        action: "analyze_elevation",
        args: {
          action: "analyze_elevation",
          name: rawArgs.name,
          ranges: rawArgs.ranges,
          numRanges: rawArgs.numRanges,
        },
      }),
    },
    {
      toolName: "civil3d_surface_analyze_directions",
      displayName: "Civil 3D Surface Analyze Directions",
      description: "Analyze aspect and facing-direction distribution across a Civil 3D surface.",
      inputShape: {
        name: z.string(),
        numRanges: z.number().int().min(4).max(16).optional(),
      },
      supportedActions: ["analyze_directions"],
      resolveAction: (rawArgs) => ({
        action: "analyze_directions",
        args: {
          action: "analyze_directions",
          name: rawArgs.name,
          numRanges: rawArgs.numRanges,
        },
      }),
    },
    {
      toolName: "civil3d_surface_watershed_add",
      displayName: "Civil 3D Surface Watershed Add",
      description: "Adds watershed analysis results to a Civil 3D surface.",
      inputShape: {
        name: z.string(),
        depthThreshold: z.number().positive().optional(),
        mergeAdjacentWatersheds: z.boolean().optional(),
      },
      supportedActions: ["watershed_add"],
      resolveAction: (rawArgs) => ({
        action: "watershed_add",
        args: {
          action: "watershed_add",
          name: rawArgs.name,
          depthThreshold: rawArgs.depthThreshold,
          mergeAdjacentWatersheds: rawArgs.mergeAdjacentWatersheds,
        },
      }),
    },
    {
      toolName: "civil3d_surface_contour_interval_set",
      displayName: "Civil 3D Surface Contour Interval Set",
      description: "Set minor and major contour display intervals for a Civil 3D surface.",
      inputShape: {
        name: z.string(),
        minorInterval: z.number().positive(),
        majorInterval: z.number().positive(),
      },
      supportedActions: ["contour_interval_set"],
      resolveAction: (rawArgs) => ({
        action: "contour_interval_set",
        args: {
          action: "contour_interval_set",
          name: rawArgs.name,
          minorInterval: rawArgs.minorInterval,
          majorInterval: rawArgs.majorInterval,
        },
      }),
    },
    {
      toolName: "civil3d_surface_statistics_get",
      displayName: "Civil 3D Surface Statistics Get",
      description: "Retrieve detailed statistics for a single Civil 3D surface.",
      inputShape: {
        name: z.string(),
      },
      supportedActions: ["statistics_get"],
      resolveAction: (rawArgs) => ({
        action: "statistics_get",
        args: {
          action: "statistics_get",
          name: rawArgs.name,
        },
      }),
    },
    {
      toolName: "civil3d_surface_sample_elevations",
      displayName: "Civil 3D Surface Sample Elevations",
      description: "Sample elevations on a Civil 3D surface using grid, point, or transect methods.",
      inputShape: {
        name: z.string(),
        method: z.enum(["grid", "points", "transect"]),
        gridSpacing: z.number().positive().optional(),
        points: z.array(Point2DSchema).optional(),
        startPoint: Point2DSchema.optional(),
        endPoint: Point2DSchema.optional(),
        numSamples: z.number().int().min(2).optional(),
        boundary: z.array(Point2DSchema).optional(),
      },
      supportedActions: ["sample_elevations"],
      resolveAction: (rawArgs) => ({
        action: "sample_elevations",
        args: {
          action: "sample_elevations",
          name: rawArgs.name,
          method: rawArgs.method,
          gridSpacing: rawArgs.gridSpacing,
          points: rawArgs.points,
          startPoint: rawArgs.startPoint,
          endPoint: rawArgs.endPoint,
          numSamples: rawArgs.numSamples,
          boundary: rawArgs.boundary,
        },
      }),
    },
    {
      toolName: "civil3d_surface_create_from_dem",
      displayName: "Civil 3D Surface Create From DEM",
      description: "Create a Civil 3D TIN surface by importing a DEM or raster terrain file.",
      inputShape: {
        filePath: z.string(),
        name: z.string(),
        style: z.string().optional(),
        layer: z.string().optional(),
        description: z.string().optional(),
        coordinateSystem: z.string().optional(),
      },
      supportedActions: ["create_from_dem"],
      resolveAction: (rawArgs) => ({
        action: "create_from_dem",
        args: {
          action: "create_from_dem",
          filePath: rawArgs.filePath,
          name: rawArgs.name,
          style: rawArgs.style,
          layer: rawArgs.layer,
          description: rawArgs.description,
          coordinateSystem: rawArgs.coordinateSystem,
        },
      }),
    },
    {
      toolName: "civil3d_surface_comparison_workflow",
      displayName: "Civil 3D Surface Comparison Workflow",
      description: "Builds a structured surface comparison by fetching two surfaces and computing cut/fill differences.",
      inputShape: {
        baseSurface: z.string(),
        comparisonSurface: z.string(),
      },
      supportedActions: ["comparison_workflow"],
      resolveAction: (rawArgs) => ({
        action: "comparison_workflow",
        args: {
          action: "comparison_workflow",
          baseSurface: rawArgs.baseSurface,
          comparisonSurface: rawArgs.comparisonSurface,
        },
      }),
    },
    {
      toolName: "civil3d_surface_drainage_workflow",
      displayName: "Civil 3D Surface Drainage Workflow",
      description: "Runs a surface drainage workflow by tracing a flow path, sampling elevations, and estimating runoff.",
      inputShape: {
        surfaceName: z.string(),
        x: z.number(),
        y: z.number(),
        stepDistance: z.number().positive().optional(),
        maxSteps: z.number().int().positive().optional(),
        drainageArea: z.number().positive(),
        runoffCoefficient: z.number().min(0).max(1),
        rainfallIntensity: z.number().positive(),
        areaUnits: z.enum(["acres", "hectares"]),
        intensityUnits: z.enum(["in_per_hr", "mm_per_hr"]),
      },
      supportedActions: ["drainage_workflow"],
      resolveAction: (rawArgs) => ({
        action: "drainage_workflow",
        args: {
          action: "drainage_workflow",
          name: rawArgs.surfaceName,
          x: rawArgs.x,
          y: rawArgs.y,
          stepDistance: rawArgs.stepDistance,
          maxSteps: rawArgs.maxSteps,
          drainageArea: rawArgs.drainageArea,
          runoffCoefficient: rawArgs.runoffCoefficient,
          rainfallIntensity: rawArgs.rainfallIntensity,
          areaUnits: rawArgs.areaUnits,
          intensityUnits: rawArgs.intensityUnits,
        },
      }),
    },
  ],
};
