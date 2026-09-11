using System.Collections.Concurrent;
using System.Reflection;

namespace Civil3DMcpPlugin;

/// <summary>
/// Narrow compatibility boundary for Civil 3D members that are unavailable in
/// the referenced managed API or vary between supported host versions.
/// Documented Autodesk members should be called directly instead.
/// </summary>
internal static class Civil3DCompatibility
{
  internal readonly record struct ParameterShape(Type Type);
  private sealed record CachedProperty(PropertyInfo? Value);
  private sealed record CachedMethods(MethodInfo[] Values);
  private sealed record CachedField(FieldInfo? Value);
  private sealed record CachedType(Type? Value);

  private readonly record struct PropertyKey(Type Type, string Name, bool IsStatic);
  private readonly record struct MethodKey(Type Type, string Name, bool IsStatic, int ArgumentCount);
  private readonly record struct MethodFamilyKey(Type Type, string Name, bool IsStatic);
  private readonly record struct LoadedStaticMethodKey(string Name, Type FirstParameterType, int ArgumentCount);

  private static readonly ConcurrentDictionary<PropertyKey, CachedProperty> PropertyCache = new();
  private static readonly ConcurrentDictionary<PropertyKey, CachedField> FieldCache = new();
  private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ScalarPropertyCache = new();
  private static readonly ConcurrentDictionary<MethodKey, CachedMethods> MethodCache = new();
  private static readonly ConcurrentDictionary<MethodFamilyKey, CachedMethods> MethodFamilyCache = new();
  private static readonly ConcurrentDictionary<LoadedStaticMethodKey, CachedMethods> LoadedStaticMethodCache = new();
  private static readonly ConcurrentDictionary<string, CachedType> TypeCache = new(StringComparer.Ordinal);

  public static T? GetPropertyValue<T>(object? target, string propertyName)
  {
    var raw = GetPropertyValue(target, propertyName);
    if (raw == null)
    {
      return default;
    }

    if (raw is T typed)
    {
      return typed;
    }

    try
    {
      var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
      if (targetType.IsInstanceOfType(raw))
      {
        return (T)(object)raw;
      }

      return (T)(object)Convert.ChangeType(raw, targetType)!;
    }
    catch
    {
      return default;
    }
  }

  public static object? GetPropertyValue(object? target, string propertyName)
  {
    if (target == null)
    {
      return null;
    }

    var property = ResolveProperty(target.GetType(), propertyName, isStatic: false);
    if (property == null)
    {
      return null;
    }

    try
    {
      return property.GetValue(target);
    }
    catch
    {
      return null;
    }
  }

  public static object? GetIndexedPropertyValue(object? target, string propertyName, params object?[] indexes)
  {
    if (target == null)
    {
      return null;
    }

    var property = ResolveProperty(target.GetType(), propertyName, isStatic: false);
    try
    {
      return property?.GetValue(target, indexes);
    }
    catch
    {
      return null;
    }
  }

  public static object? GetFieldValue(object? target, string fieldName)
  {
    if (target == null)
    {
      return null;
    }

    var type = target.GetType();
    var key = new PropertyKey(type, fieldName, IsStatic: false);
    var field = FieldCache.GetOrAdd(key, static item =>
      new CachedField(item.Type.GetField(item.Name, BindingFlags.Public | BindingFlags.Instance))).Value;
    try
    {
      return field?.GetValue(target);
    }
    catch
    {
      return null;
    }
  }

  public static bool TrySetProperty(object? target, string propertyName, object? value)
  {
    if (target == null)
    {
      return false;
    }

    var property = ResolveProperty(target.GetType(), propertyName, isStatic: false);
    if (property?.CanWrite != true)
    {
      return false;
    }

    try
    {
      var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
      var converted = value == null || targetType.IsInstanceOfType(value)
        ? value
        : targetType.IsEnum && value is string enumText
          ? Enum.Parse(targetType, enumText, ignoreCase: true)
          : Convert.ChangeType(value, targetType);
      property.SetValue(target, converted);
      return true;
    }
    catch
    {
      return false;
    }
  }

  public static IReadOnlyDictionary<string, object?> GetReadableScalarProperties(object target)
  {
    var properties = ScalarPropertyCache.GetOrAdd(target.GetType(), static type =>
      type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
        .Where(property =>
        {
          var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
          return propertyType == typeof(string)
            || propertyType == typeof(bool)
            || propertyType == typeof(int)
            || propertyType == typeof(double);
        })
        .ToArray());

    var values = new Dictionary<string, object?>(StringComparer.Ordinal);
    foreach (var property in properties)
    {
      try
      {
        values[property.Name] = property.GetValue(target);
      }
      catch
      {
        // Some Autodesk wrappers throw for state-dependent getters. Omit them.
      }
    }

    return values;
  }

  public static object? InvokeMethod(object? target, string methodName, params object?[] arguments)
  {
    if (target == null)
    {
      return null;
    }

    TryInvokeCandidates(target.GetType(), target, methodName, isStatic: false, arguments, out var result);
    return result;
  }

  public static bool TryInvokeMethod(object? target, string methodName, out object? result, params object?[] arguments)
  {
    result = null;
    return target != null
      && TryInvokeCandidates(target.GetType(), target, methodName, isStatic: false, arguments, out result);
  }

  public static object? InvokeStaticMethod(Type type, string methodName, params object?[] arguments)
  {
    TryInvokeCandidates(type, null, methodName, isStatic: true, arguments, out var result);
    return result;
  }

  public static bool TryInvokeStaticMethod(Type type, string methodName, out object? result, params object?[] arguments)
  {
    return TryInvokeCandidates(type, null, methodName, isStatic: true, arguments, out result);
  }

  public static bool TryInvokeStaticOverloads(
    Type type,
    string methodName,
    Func<ParameterShape[], object?[]?> argumentBuilder,
    out object? result,
    out object?[]? invokedArguments)
  {
    result = null;
    invokedArguments = null;
    var key = new MethodFamilyKey(type, methodName, IsStatic: true);
    var methods = MethodFamilyCache.GetOrAdd(key, static item =>
      new CachedMethods(item.Type.GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.Name == item.Name)
        .OrderBy(method => method.GetParameters().Length)
        .ToArray())).Values;

    foreach (var method in methods)
    {
      var shapes = method.GetParameters()
        .Select(parameter => new ParameterShape(
          parameter.ParameterType.IsByRef
            ? parameter.ParameterType.GetElementType()!
            : parameter.ParameterType))
        .ToArray();
      var arguments = argumentBuilder(shapes);
      if (arguments == null)
      {
        continue;
      }

      try
      {
        result = method.Invoke(null, arguments);
        invokedArguments = arguments;
        return true;
      }
      catch (ArgumentException)
      {
      }
      catch (TargetParameterCountException)
      {
      }
      catch (TargetInvocationException)
      {
        // The runtime-selected overload rejected its synthesized defaults.
        // Continue so another compatible Civil 3D overload can be attempted.
      }
    }

    return false;
  }

  public static bool TryInvokeLoadedStaticMethod(
    string methodName,
    Type firstParameterType,
    out object? result,
    params object?[] arguments)
  {
    result = null;
    var key = new LoadedStaticMethodKey(methodName, firstParameterType, arguments.Length);
    var methods = LoadedStaticMethodCache.GetOrAdd(key, static item =>
    {
      var matches = new List<MethodInfo>();
      foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
      {
        Type[] types;
        try
        {
          types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
          types = exception.Types.Where(type => type != null).Cast<Type>().ToArray();
        }
        catch
        {
          continue;
        }

        foreach (var type in types)
        {
          matches.AddRange(type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == item.Name)
            .Where(method => method.GetParameters().Length == item.ArgumentCount)
            .Where(method => method.GetParameters().Length > 0
              && method.GetParameters()[0].ParameterType.IsAssignableFrom(item.FirstParameterType)));
        }
      }
      return new CachedMethods(matches.ToArray());
    }).Values;

    foreach (var method in methods)
    {
      try
      {
        result = method.Invoke(null, arguments);
        return true;
      }
      catch (ArgumentException)
      {
      }
      catch (TargetParameterCountException)
      {
      }
    }
    return false;
  }

  public static Type? FindLoadedType(params string[] fullNames)
  {
    foreach (var fullName in fullNames)
    {
      var cached = TypeCache.GetOrAdd(fullName, static candidateName =>
      {
        var assemblyQualifiedType = Type.GetType(candidateName, throwOnError: false, ignoreCase: false);
        if (assemblyQualifiedType != null)
        {
          return new CachedType(assemblyQualifiedType);
        }

        var commaIndex = candidateName.IndexOf(',');
        var normalizedName = commaIndex >= 0
          ? candidateName[..commaIndex].Trim()
          : candidateName;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
          var type = assembly.GetType(normalizedName, throwOnError: false, ignoreCase: false);
          if (type != null)
          {
            return new CachedType(type);
          }
        }

        return new CachedType(null);
      });
      if (cached.Value != null)
      {
        return cached.Value;
      }
    }

    return null;
  }

  private static PropertyInfo? ResolveProperty(Type type, string propertyName, bool isStatic)
  {
    var key = new PropertyKey(type, propertyName, isStatic);
    return PropertyCache.GetOrAdd(key, static item =>
    {
      var flags = BindingFlags.Public | (item.IsStatic ? BindingFlags.Static : BindingFlags.Instance);
      return new CachedProperty(item.Type.GetProperty(item.Name, flags));
    }).Value;
  }

  private static bool TryInvokeCandidates(
    Type type,
    object? target,
    string methodName,
    bool isStatic,
    object?[] arguments,
    out object? result)
  {
    result = null;
    var key = new MethodKey(type, methodName, isStatic, arguments.Length);
    var methods = MethodCache.GetOrAdd(key, static item =>
    {
      var flags = BindingFlags.Public | (item.IsStatic ? BindingFlags.Static : BindingFlags.Instance);
      return new CachedMethods(item.Type
        .GetMethods(flags)
        .Where(method => method.Name == item.Name && method.GetParameters().Length == item.ArgumentCount)
        .ToArray());
    }).Values;

    foreach (var method in methods)
    {
      try
      {
        result = method.Invoke(target, arguments);
        return true;
      }
      catch (ArgumentException)
      {
        // Try the next overload. Invocation exceptions from a compatible
        // overload are allowed to propagate to the command's explicit error path.
      }
      catch (TargetParameterCountException)
      {
      }
    }

    return false;
  }

  // ─── Diagnostic reads (added for AlignmentGeometryReader) ────────────────────
  // Unlike GetPropertyValue, these report *why* a read failed instead of returning null,
  // and resolve hidden ("new") properties to the most-derived declaration.

  public static object? TryReadProperty(object? target, string propertyName, out string? error)
  {
    error = null;
    if (target == null)
    {
      error = "target is null";
      return null;
    }

    try
    {
      var property = FindMostDerivedProperty(target.GetType(), propertyName);
      if (property == null)
      {
        error = "property not found";
        return null;
      }

      return property.GetValue(target);
    }
    catch (Exception ex)
    {
      error = DescribeException(ex);
      return null;
    }
  }

  public static object? TryReadIntIndexer(object target, int index, out string? error)
  {
    error = null;
    try
    {
      var indexer = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .FirstOrDefault(property =>
        {
          var parameters = property.GetIndexParameters();
          return property.CanRead && parameters.Length == 1 && parameters[0].ParameterType == typeof(int);
        });
      if (indexer != null)
      {
        return indexer.GetValue(target, new object[] { index });
      }

      var method = target.GetType().GetMethod("GetSubEntity", new[] { typeof(int) });
      if (method != null)
      {
        return method.Invoke(target, new object[] { index });
      }

      error = "no int indexer or GetSubEntity(int) found";
      return null;
    }
    catch (Exception ex)
    {
      error = DescribeException(ex);
      return null;
    }
  }

  public static Dictionary<string, object?> ReadAllPropertiesForReport(object target, IDictionary<string, string> errors)
  {
    var values = new Dictionary<string, object?>(StringComparer.Ordinal);
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
      .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
      .OrderByDescending(property => InheritanceDepth(property.DeclaringType)))
    {
      if (!seen.Add(property.Name))
      {
        continue;
      }

      try
      {
        values[property.Name] = ConvertForReport(property.GetValue(target));
      }
      catch (Exception ex)
      {
        errors[property.Name] = DescribeException(ex);
      }
    }

    return values;
  }

  // Civil 3D style objects do not expose "Name" to plain GetProperty lookups (live-tested on
  // 2026: every style name came back null). Try the other ways a name can be published, cache
  // whichever works per type, and describe what exists when nothing does.
  private static readonly ConcurrentDictionary<Type, Func<object, object?>?> NameAccessorCache = new();

  public static string? TryReadName(object? target, out string? diagnostics)
  {
    diagnostics = null;
    if (target == null)
    {
      return null;
    }

    var type = target.GetType();
    if (NameAccessorCache.TryGetValue(type, out var cached) && cached != null)
    {
      try
      {
        if (cached(target) is string cachedName && !string.IsNullOrEmpty(cachedName))
        {
          return cachedName;
        }
      }
      catch
      {
        // Fall through to a full search; the object may be in a state the cached accessor rejects.
      }
    }

    var attempts = new List<string>();
    foreach (var (label, accessor) in EnumerateNameAccessors(type))
    {
      try
      {
        if (accessor(target) is string name && !string.IsNullOrEmpty(name))
        {
          NameAccessorCache[type] = accessor;
          return name;
        }

        attempts.Add($"{label}: empty");
      }
      catch (Exception ex)
      {
        attempts.Add($"{label}: {DescribeException(ex)}");
      }
    }

    var nameLikeMembers = type
      .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
      .Where(member => member.Name.IndexOf("name", StringComparison.OrdinalIgnoreCase) >= 0)
      .Select(member => $"{member.MemberType}:{member.DeclaringType?.Name}.{member.Name}")
      .Distinct()
      .Take(30);
    diagnostics = $"{type.FullName} | tried: {(attempts.Count > 0 ? string.Join("; ", attempts) : "no accessors found")} " +
      $"| name-like members: {string.Join(", ", nameLikeMembers)}";
    return null;
  }

  private static IEnumerable<(string Label, Func<object, object?> Accessor)> EnumerateNameAccessors(Type type)
  {
    var publicProperty = FindMostDerivedProperty(type, "Name");
    if (publicProperty != null)
    {
      yield return ($"public {publicProperty.DeclaringType?.Name}.Name", target => publicProperty.GetValue(target));
    }

    const BindingFlags declaredInstance =
      BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    for (var current = type; current != null && current != typeof(object); current = current.BaseType)
    {
      var getter = current.GetMethod("get_Name", declaredInstance, null, Type.EmptyTypes, null);
      if (getter != null)
      {
        yield return ($"{current.Name}.get_Name()", target => getter.Invoke(target, null));
      }

      foreach (var property in current.GetProperties(declaredInstance)
        .Where(property => property.CanRead
          && property.GetIndexParameters().Length == 0
          && property.Name.EndsWith(".Name", StringComparison.Ordinal)))
      {
        yield return ($"{current.Name}.{property.Name}", target => property.GetValue(target));
      }
    }

    foreach (var implemented in type.GetInterfaces())
    {
      var interfaceProperty = implemented.GetProperty("Name");
      if (interfaceProperty != null && interfaceProperty.CanRead && interfaceProperty.GetIndexParameters().Length == 0)
      {
        yield return ($"{implemented.Name}.Name", target => interfaceProperty.GetValue(target));
      }
    }
  }

  // ─── Overload-tolerant invocation (added for the UTNM alignment builder) ─────
  // Calls an API method by matching argument *shapes* against the overloads that actually exist at
  // runtime. Enum parameters accept names ("Radius", "Compound|Reverse|*" where * = first value).
  // If nothing matches, the error lists every real overload with enum members, so a signature
  // difference in a Civil 3D release shows up as a readable message instead of a compile failure.

  public static object? InvokeMatchingOverload(object? target, Type? staticType, string methodName, object?[] arguments, out string? error)
  {
    error = null;
    var type = target?.GetType() ?? staticType;
    if (type == null)
    {
      error = "no target or type";
      return null;
    }

    var flags = BindingFlags.Public | (target == null ? BindingFlags.Static : BindingFlags.Instance);
    foreach (var method in type.GetMethods(flags)
      .Where(method => method.Name == methodName && method.GetParameters().Length == arguments.Length))
    {
      var parameters = method.GetParameters();
      var converted = new object?[arguments.Length];
      var matches = true;
      for (var i = 0; i < parameters.Length && matches; i++)
      {
        matches = TryConvertArgument(arguments[i], parameters[i].ParameterType, out converted[i]);
      }

      if (!matches)
      {
        continue;
      }

      try
      {
        return method.Invoke(target, converted);
      }
      catch (Exception ex)
      {
        error = DescribeException(ex);
        return null;
      }
    }

    error = $"no overload of {type.Name}.{methodName} accepts ({string.Join(", ", arguments.Select(argument => argument?.GetType().Name ?? "null"))}). " +
      $"Available: {DescribeOverloads(type, methodName, flags)}";
    return null;
  }

  public static bool TrySetPropertyValue(object? target, string propertyName, object? value, out string? error)
  {
    error = null;
    if (target == null)
    {
      error = "target is null";
      return false;
    }

    const BindingFlags declaredInstance =
      BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    for (var current = target.GetType(); current != null && current != typeof(object); current = current.BaseType)
    {
      var property = current.GetProperty(propertyName, declaredInstance);
      var setter = property?.GetSetMethod(nonPublic: true) ?? current.GetMethod($"set_{propertyName}", declaredInstance);
      if (setter == null)
      {
        continue;
      }

      var parameterType = setter.GetParameters().FirstOrDefault()?.ParameterType;
      if (parameterType == null || !TryConvertArgument(value, parameterType, out var converted))
      {
        error = $"{current.Name}.{propertyName} does not accept {value?.GetType().Name ?? "null"}";
        return false;
      }

      try
      {
        setter.Invoke(target, new[] { converted });
        return true;
      }
      catch (Exception ex)
      {
        error = DescribeException(ex);
        return false;
      }
    }

    error = "no setter found";
    return false;
  }

  private static bool TryConvertArgument(object? argument, Type parameterType, out object? converted)
  {
    converted = null;
    var targetType = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
    if (argument == null)
    {
      return !parameterType.IsValueType || Nullable.GetUnderlyingType(parameterType) != null;
    }

    if (targetType.IsInstanceOfType(argument))
    {
      converted = argument;
      return true;
    }

    if (targetType.IsEnum && argument is string enumNames)
    {
      foreach (var candidate in enumNames.Split('|'))
      {
        if (candidate == "*")
        {
          var values = Enum.GetValues(targetType);
          if (values.Length > 0)
          {
            converted = values.GetValue(0);
            return true;
          }
        }
        else if (Enum.TryParse(targetType, candidate, ignoreCase: true, out var parsed))
        {
          converted = parsed;
          return true;
        }
      }

      return false;
    }

    if ((targetType == typeof(double) || targetType == typeof(float) || targetType == typeof(int))
      && argument is double or float or int or long)
    {
      if (targetType == typeof(int) && argument is double or float)
      {
        return false;
      }

      converted = Convert.ChangeType(argument, targetType);
      return true;
    }

    return false;
  }

  private static string DescribeOverloads(Type type, string methodName, BindingFlags flags)
  {
    var overloads = type.GetMethods(flags)
      .Where(method => method.Name == methodName)
      .Select(method => $"{method.Name}(" + string.Join(", ", method.GetParameters().Select(parameter =>
      {
        var parameterType = parameter.ParameterType;
        var enumMembers = parameterType.IsEnum ? "{" + string.Join("|", Enum.GetNames(parameterType)) + "}" : string.Empty;
        return $"{parameterType.Name}{enumMembers} {parameter.Name}";
      })) + ")")
      .ToList();
    return overloads.Count > 0 ? string.Join(" ; ", overloads) : "none";
  }

  public static string DescribeException(Exception ex)
  {
    var inner = ex is TargetInvocationException { InnerException: not null } ? ex.InnerException! : ex;
    return $"{inner.GetType().Name}: {inner.Message}";
  }

  private static PropertyInfo? FindMostDerivedProperty(Type type, string propertyName)
  {
    return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
      .Where(property => property.Name == propertyName && property.CanRead && property.GetIndexParameters().Length == 0)
      .OrderByDescending(property => InheritanceDepth(property.DeclaringType))
      .FirstOrDefault();
  }

  private static int InheritanceDepth(Type? type)
  {
    var depth = 0;
    while (type != null)
    {
      depth++;
      type = type.BaseType;
    }

    return depth;
  }

  public static object? ConvertForReport(object? value)
  {
    switch (value)
    {
      case null:
        return null;
      case string text:
        return text;
      case bool flag:
        return flag;
      case double number:
        return double.IsFinite(number) ? number : number.ToString();
      case float single:
        return float.IsFinite(single) ? (double)single : single.ToString();
      case int or long or short or byte or uint or ushort:
        return Convert.ToInt64(value);
      case Enum enumValue:
        return enumValue.ToString();
    }

    var type = value.GetType();
    if (type.Name is "Point2d" or "Point3d" or "Vector2d" or "Vector3d")
    {
      var axes = new Dictionary<string, object?>();
      foreach (var axis in new[] { "X", "Y", "Z" })
      {
        var axisProperty = type.GetProperty(axis, BindingFlags.Public | BindingFlags.Instance);
        if (axisProperty != null)
        {
          axes[axis.ToLowerInvariant()] = ConvertForReport(axisProperty.GetValue(value));
        }
      }

      return axes;
    }

    return $"<{type.Name}>";
  }
}
