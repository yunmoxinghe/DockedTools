// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Svg.Transforms;

namespace Svg.Model.Services;

internal static class VectorDrawableConverter
{
    internal const string AndroidNamespace = "http://schemas.android.com/apk/res/android";
    private const string AaptNamespace = "http://schemas.android.com/aapt";

    internal static bool IsVectorDrawable(XmlReader reader)
    {
        if (reader is null)
        {
            throw new ArgumentNullException(nameof(reader));
        }

        if (MoveToRootElement(reader) == false)
        {
            return false;
        }

        return IsVectorDrawable(reader.LocalName, reader.LookupNamespace("android"));
    }

    internal static SvgDocument Open(XmlReader reader)
    {
        if (reader is null)
        {
            throw new ArgumentNullException(nameof(reader));
        }

        var document = XDocument.Load(reader, LoadOptions.None);
        return Convert(document);
    }

    internal static SvgDocument Open(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        using var reader = XmlReader.Create(stream, CreateReaderSettings());
        return Open(reader);
    }

    internal static SvgDocument FromXml(string xml)
    {
        if (xml is null)
        {
            throw new ArgumentNullException(nameof(xml));
        }

        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, CreateReaderSettings());
        return Open(reader);
    }

    private static SvgDocument Convert(XDocument xDocument)
    {
        if (xDocument is null)
        {
            throw new ArgumentNullException(nameof(xDocument));
        }

        var root = xDocument.Root ?? throw new FormatException("VectorDrawable XML is missing a root element.");
        ValidateRoot(root);

        var context = new ConversionContext();
        var document = new SvgDocument();

        var rootId = GetOptionalAndroidAttribute(root, "name");
        if (!string.IsNullOrWhiteSpace(rootId))
        {
            document.ID = context.EnsureUniqueId(rootId!, "vector");
        }

        var width = ParseDimension(root, "width");
        var height = ParseDimension(root, "height");
        var viewportWidth = ParseRequiredFloat(root, "viewportWidth");
        var viewportHeight = ParseRequiredFloat(root, "viewportHeight");

        EnsurePositiveVectorDimension("width", width);
        EnsurePositiveVectorDimension("height", height);
        EnsurePositiveVectorFloat("viewportWidth", viewportWidth);
        EnsurePositiveVectorFloat("viewportHeight", viewportHeight);

        document.Width = width;
        document.Height = height;
        document.ViewBox = new SvgViewBox(
            0f,
            0f,
            viewportWidth,
            viewportHeight);
        document.Opacity = ParseOptionalFloat(root, "alpha", 1f);

        ApplyContainerChildren(document, document, root, context);
        return document;
    }

    private static void ApplyContainerChildren(SvgDocument document, SvgElement parent, XElement container, ConversionContext context)
    {
        var clipPathIds = new List<string>();
        var convertedChildren = new List<SvgElement>();

        foreach (var child in container.Elements())
        {
            EnsureSupportedElement(child);
            switch (child.Name.LocalName)
            {
                case "clip-path":
                    clipPathIds.Add(CreateClipPathDefinition(child, document, context));
                    break;
                case "group":
                    convertedChildren.Add(ConvertGroup(child, document, context));
                    break;
                case "path":
                    convertedChildren.Add(ConvertPath(child, context));
                    break;
                default:
                    throw CreateUnsupportedElementException(child);
            }
        }

        if (clipPathIds.Count == 0)
        {
            foreach (var child in convertedChildren)
            {
                parent.Children.Add(child);
            }

            return;
        }

        SvgGroup? outerWrapper = null;
        SvgElement? clipTarget = null;
        foreach (var clipPathId in clipPathIds)
        {
            var wrapper = new SvgGroup
            {
                ClipPath = new Uri($"#{clipPathId}", UriKind.Relative)
            };

            if (outerWrapper is null)
            {
                outerWrapper = wrapper;
                clipTarget = wrapper;
            }
            else
            {
                clipTarget!.Children.Add(wrapper);
                clipTarget = wrapper;
            }
        }

        foreach (var child in convertedChildren)
        {
            clipTarget!.Children.Add(child);
        }

        parent.Children.Add(outerWrapper!);
    }

    private static SvgGroup ConvertGroup(XElement element, SvgDocument document, ConversionContext context)
    {
        ValidateAttributes(
            element,
            "name",
            "rotation",
            "pivotX",
            "pivotY",
            "scaleX",
            "scaleY",
            "translateX",
            "translateY");

        var group = new SvgGroup();

        var name = GetOptionalAndroidAttribute(element, "name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            group.ID = context.EnsureUniqueId(name!, "group");
        }

        var matrix = BuildGroupTransform(element);
        if (matrix is not null)
        {
            group.Transforms = new SvgTransformCollection { matrix };
        }

        ApplyContainerChildren(document, group, element, context);
        return group;
    }

    private static SvgPath ConvertPath(XElement element, ConversionContext context)
    {
        ValidateAttributes(
            element,
            "name",
            "pathData",
            "fillColor",
            "strokeColor",
            "strokeWidth",
            "strokeAlpha",
            "fillAlpha",
            "strokeLineCap",
            "strokeLineJoin",
            "strokeMiterLimit",
            "fillType",
            "trimPathStart",
            "trimPathEnd",
            "trimPathOffset");

        EnsureUnsupportedAttributeAbsent(element, "trimPathStart", "trimPathStart is not supported.");
        EnsureUnsupportedAttributeAbsent(element, "trimPathEnd", "trimPathEnd is not supported.");
        EnsureUnsupportedAttributeAbsent(element, "trimPathOffset", "trimPathOffset is not supported.");

        var path = new SvgPath
        {
            Fill = SvgPaintServer.None,
            Stroke = SvgPaintServer.None,
            PathData = SvgPathBuilder.Parse(ParseRequiredString(element, "pathData").AsSpan())
        };

        var name = GetOptionalAndroidAttribute(element, "name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            path.ID = context.EnsureUniqueId(name!, "path");
        }

        if (TryGetAndroidAttribute(element, "fillColor", out var fillColorValue))
        {
            var fillColor = ParseAndroidColor(element, "fillColor", fillColorValue!);
            path.Fill = new SvgColourServer(System.Drawing.Color.FromArgb(255, fillColor.R, fillColor.G, fillColor.B));
            path.FillOpacity = CombineOpacity(ParseOptionalFloat(element, "fillAlpha", 1f), fillColor.A / 255f);
        }

        if (TryGetAndroidAttribute(element, "strokeColor", out var strokeColorValue))
        {
            var strokeColor = ParseAndroidColor(element, "strokeColor", strokeColorValue!);
            path.Stroke = new SvgColourServer(System.Drawing.Color.FromArgb(255, strokeColor.R, strokeColor.G, strokeColor.B));
            path.StrokeOpacity = CombineOpacity(ParseOptionalFloat(element, "strokeAlpha", 1f), strokeColor.A / 255f);
        }

        if (TryGetAndroidAttribute(element, "strokeWidth", out _))
        {
            path.StrokeWidth = ParseDimensionLikeFloat(element, "strokeWidth");
        }

        if (TryGetAndroidAttribute(element, "fillType", out var fillTypeValue))
        {
            path.FillRule = ParseFillRule(element, "fillType", fillTypeValue!);
        }

        if (TryGetAndroidAttribute(element, "strokeLineCap", out var lineCapValue))
        {
            path.StrokeLineCap = ParseStrokeLineCap(element, "strokeLineCap", lineCapValue!);
        }

        if (TryGetAndroidAttribute(element, "strokeLineJoin", out var lineJoinValue))
        {
            path.StrokeLineJoin = ParseStrokeLineJoin(element, "strokeLineJoin", lineJoinValue!);
        }

        if (TryGetAndroidAttribute(element, "strokeMiterLimit", out var miterValue))
        {
            path.StrokeMiterLimit = ParseFloat(element, "strokeMiterLimit", miterValue!);
        }

        return path;
    }

    private static string CreateClipPathDefinition(XElement element, SvgDocument document, ConversionContext context)
    {
        ValidateAttributes(element, "name", "pathData", "fillType");

        var clipPathId = GetOptionalAndroidAttribute(element, "name");
        clipPathId = string.IsNullOrWhiteSpace(clipPathId)
            ? context.EnsureUniqueId("clipPath", "clipPath")
            : context.EnsureUniqueId(clipPathId!, "clipPath");

        var clipPath = new SvgClipPath
        {
            ID = clipPathId,
            ClipPathUnits = SvgCoordinateUnits.UserSpaceOnUse
        };

        var clipPathElement = new SvgPath
        {
            PathData = SvgPathBuilder.Parse(ParseRequiredString(element, "pathData").AsSpan()),
            Fill = SvgPaintServer.None,
            Stroke = SvgPaintServer.None
        };

        if (TryGetAndroidAttribute(element, "fillType", out var fillTypeValue))
        {
            clipPathElement.ClipRule = ParseClipRule(element, "fillType", fillTypeValue!);
        }

        clipPath.Children.Add(clipPathElement);
        context.GetDefinitions(document).Children.Add(clipPath);
        return clipPathId;
    }

    private static SvgMatrix? BuildGroupTransform(XElement element)
    {
        var rotation = ParseOptionalFloat(element, "rotation", 0f);
        var pivotX = ParseOptionalFloat(element, "pivotX", 0f);
        var pivotY = ParseOptionalFloat(element, "pivotY", 0f);
        var scaleX = ParseOptionalFloat(element, "scaleX", 1f);
        var scaleY = ParseOptionalFloat(element, "scaleY", 1f);
        var translateX = ParseOptionalFloat(element, "translateX", 0f);
        var translateY = ParseOptionalFloat(element, "translateY", 0f);

        if (rotation == 0f
            && pivotX == 0f
            && pivotY == 0f
            && scaleX == 1f
            && scaleY == 1f
            && translateX == 0f
            && translateY == 0f)
        {
            return null;
        }

        var radians = DegreesToRadians(rotation);
        var cos = (float)Math.Cos(radians);
        var sin = (float)Math.Sin(radians);
        var m11 = scaleX * cos;
        var m12 = scaleX * sin;
        var m21 = -scaleY * sin;
        var m22 = scaleY * cos;
        var m31 = translateX + pivotX - (pivotX * scaleX * cos) + (pivotY * scaleY * sin);
        var m32 = translateY + pivotY - (pivotX * scaleX * sin) - (pivotY * scaleY * cos);

        return new SvgMatrix(
            new List<float>
            {
                m11,
                m12,
                m21,
                m22,
                m31,
                m32
            });
    }

    private static void ValidateRoot(XElement root)
    {
        if (root.Name.LocalName == "animated-vector")
        {
            throw new NotSupportedException("AnimatedVectorDrawable is not supported.");
        }

        if (IsVectorDrawable(root.Name.LocalName, GetNamespace(root, "android")) == false)
        {
            throw new FormatException("The provided XML is not an Android VectorDrawable document.");
        }

        ValidateAttributes(root, "name", "width", "height", "viewportWidth", "viewportHeight", "alpha", "tint", "tintMode", "autoMirrored");

        EnsureUnsupportedAttributeAbsent(root, "tint", "android:tint is not supported.");
        EnsureUnsupportedAttributeAbsent(root, "tintMode", "android:tintMode is not supported.");

        if (TryGetAndroidAttribute(root, "autoMirrored", out var autoMirroredValue)
            && bool.TryParse(autoMirroredValue, out var autoMirrored))
        {
            if (autoMirrored)
            {
                throw new NotSupportedException("android:autoMirrored=\"true\" is not supported.");
            }
        }
        else if (TryGetAndroidAttribute(root, "autoMirrored", out autoMirroredValue))
        {
            throw new FormatException($"Invalid boolean value '{autoMirroredValue}' for android:autoMirrored on <vector>.");
        }
    }

    private static void EnsureSupportedElement(XElement element)
    {
        if (element.Name.NamespaceName == AaptNamespace && element.Name.LocalName == "attr")
        {
            throw new NotSupportedException("aapt:attr is not supported.");
        }

        if (element.Name.LocalName == "animated-vector")
        {
            throw new NotSupportedException("AnimatedVectorDrawable is not supported.");
        }

        if (element.Name.LocalName is not ("group" or "path" or "clip-path"))
        {
            throw CreateUnsupportedElementException(element);
        }
    }

    private static Exception CreateUnsupportedElementException(XElement element)
    {
        var name = string.IsNullOrEmpty(element.Name.NamespaceName)
            ? element.Name.LocalName
            : $"{element.GetPrefixOfNamespace(element.Name.Namespace)}:{element.Name.LocalName}";
        return new NotSupportedException($"Unsupported VectorDrawable element '{name}'.");
    }

    private static void ValidateAttributes(XElement element, params string[] supportedAttributes)
    {
        var supported = new HashSet<string>(supportedAttributes, StringComparer.Ordinal);

        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                continue;
            }

            if (attribute.Name.NamespaceName == AndroidNamespace)
            {
                if (supported.Contains(attribute.Name.LocalName) == false)
                {
                    throw new NotSupportedException(
                        $"Unsupported VectorDrawable attribute 'android:{attribute.Name.LocalName}' on <{element.Name.LocalName}>.");
                }

                EnsureLiteralAttributeValue(element, attribute.Name.LocalName, attribute.Value);
            }
            else if (attribute.Name.NamespaceName == AaptNamespace)
            {
                throw new NotSupportedException($"Unsupported VectorDrawable attribute 'aapt:{attribute.Name.LocalName}'.");
            }
        }
    }

    private static void EnsureUnsupportedAttributeAbsent(XElement element, string attributeName, string message)
    {
        if (TryGetAndroidAttribute(element, attributeName, out _))
        {
            throw new NotSupportedException(message);
        }
    }

    private static void EnsureLiteralAttributeValue(XElement element, string attributeName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("@", StringComparison.Ordinal) || trimmed.StartsWith("?", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Resource and theme references are not supported for android:{attributeName} on <{element.Name.LocalName}>.");
        }
    }

    private static SvgUnit ParseDimension(XElement element, string attributeName)
    {
        var value = ParseRequiredString(element, attributeName);
        return ParseSvgUnit(element, attributeName, value);
    }

    private static SvgUnit ParseDimensionLikeFloat(XElement element, string attributeName)
    {
        var value = ParseRequiredString(element, attributeName);
        return ParseSvgUnit(element, attributeName, value);
    }

    private static SvgUnit ParseSvgUnit(XElement element, string attributeName, string value)
    {
        var (numericValue, unit) = ParseNumericWithUnit(element, attributeName, value);
        return unit switch
        {
            "" or "px" or "dp" or "dip" or "sp" => new SvgUnit(SvgUnitType.Pixel, numericValue),
            "pt" => new SvgUnit(SvgUnitType.Point, numericValue),
            "pc" => new SvgUnit(SvgUnitType.Pica, numericValue),
            "in" => new SvgUnit(SvgUnitType.Inch, numericValue),
            "mm" => new SvgUnit(SvgUnitType.Millimeter, numericValue),
            "cm" => new SvgUnit(SvgUnitType.Centimeter, numericValue),
            _ => throw new NotSupportedException(
                $"Unsupported unit '{unit}' for android:{attributeName} on <{element.Name.LocalName}>.")
        };
    }

    private static float ParseRequiredFloat(XElement element, string attributeName)
    {
        var value = ParseRequiredString(element, attributeName);
        return ParseFloat(element, attributeName, value);
    }

    private static float ParseOptionalFloat(XElement element, string attributeName, float defaultValue)
    {
        return TryGetAndroidAttribute(element, attributeName, out var value)
            ? ParseFloat(element, attributeName, value!)
            : defaultValue;
    }

    private static float ParseFloat(XElement element, string attributeName, string value)
    {
        EnsureLiteralAttributeValue(element, attributeName, value);
        if (float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            if (attributeName.Contains("Alpha", StringComparison.Ordinal) || attributeName == "alpha")
            {
                return Clamp01(parsed);
            }

            return parsed;
        }

        throw new FormatException($"Invalid float value '{value}' for android:{attributeName} on <{element.Name.LocalName}>.");
    }

    private static string ParseRequiredString(XElement element, string attributeName)
    {
        if (TryGetAndroidAttribute(element, attributeName, out var value)
            && string.IsNullOrWhiteSpace(value) == false)
        {
            return value!;
        }

        throw new FormatException($"Missing required android:{attributeName} on <{element.Name.LocalName}>.");
    }

    private static SvgFillRule ParseFillRule(XElement element, string attributeName, string value)
    {
        EnsureLiteralAttributeValue(element, attributeName, value);
        return value.Trim() switch
        {
            "evenOdd" => SvgFillRule.EvenOdd,
            "nonZero" => SvgFillRule.NonZero,
            _ => throw new NotSupportedException(
                $"Unsupported fill type '{value}' for android:{attributeName} on <{element.Name.LocalName}>.")
        };
    }

    private static SvgClipRule ParseClipRule(XElement element, string attributeName, string value)
    {
        EnsureLiteralAttributeValue(element, attributeName, value);
        return value.Trim() switch
        {
            "evenOdd" => SvgClipRule.EvenOdd,
            "nonZero" => SvgClipRule.NonZero,
            _ => throw new NotSupportedException(
                $"Unsupported fill type '{value}' for android:{attributeName} on <{element.Name.LocalName}>.")
        };
    }

    private static SvgStrokeLineCap ParseStrokeLineCap(XElement element, string attributeName, string value)
    {
        EnsureLiteralAttributeValue(element, attributeName, value);
        return value.Trim() switch
        {
            "butt" => SvgStrokeLineCap.Butt,
            "round" => SvgStrokeLineCap.Round,
            "square" => SvgStrokeLineCap.Square,
            _ => throw new NotSupportedException(
                $"Unsupported stroke line cap '{value}' for android:{attributeName} on <{element.Name.LocalName}>.")
        };
    }

    private static SvgStrokeLineJoin ParseStrokeLineJoin(XElement element, string attributeName, string value)
    {
        EnsureLiteralAttributeValue(element, attributeName, value);
        return value.Trim() switch
        {
            "miter" => SvgStrokeLineJoin.Miter,
            "round" => SvgStrokeLineJoin.Round,
            "bevel" => SvgStrokeLineJoin.Bevel,
            _ => throw new NotSupportedException(
                $"Unsupported stroke line join '{value}' for android:{attributeName} on <{element.Name.LocalName}>.")
        };
    }

    private static System.Drawing.Color ParseAndroidColor(XElement element, string attributeName, string value)
    {
        EnsureLiteralAttributeValue(element, attributeName, value);

        var trimmed = value.Trim();
        if (trimmed.StartsWith("#", StringComparison.Ordinal) == false)
        {
            throw new NotSupportedException(
                $"Only hex colors are supported for android:{attributeName} on <{element.Name.LocalName}>.");
        }

        var hex = trimmed.Substring(1);
        return hex.Length switch
        {
            3 => System.Drawing.Color.FromArgb(
                255,
                ParseHexPair(hex[0], hex[0]),
                ParseHexPair(hex[1], hex[1]),
                ParseHexPair(hex[2], hex[2])),
            4 => System.Drawing.Color.FromArgb(
                ParseHexPair(hex[0], hex[0]),
                ParseHexPair(hex[1], hex[1]),
                ParseHexPair(hex[2], hex[2]),
                ParseHexPair(hex[3], hex[3])),
            6 => System.Drawing.Color.FromArgb(
                255,
                ParseHexPair(hex[0], hex[1]),
                ParseHexPair(hex[2], hex[3]),
                ParseHexPair(hex[4], hex[5])),
            8 => System.Drawing.Color.FromArgb(
                ParseHexPair(hex[0], hex[1]),
                ParseHexPair(hex[2], hex[3]),
                ParseHexPair(hex[4], hex[5]),
                ParseHexPair(hex[6], hex[7])),
            _ => throw new FormatException(
                $"Invalid color value '{value}' for android:{attributeName} on <{element.Name.LocalName}>.")
        };
    }

    private static byte ParseHexPair(char high, char low)
    {
        var chars = new[] { high, low };
        return byte.Parse(new string(chars), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static (float NumericValue, string Unit) ParseNumericWithUnit(XElement element, string attributeName, string value)
    {
        EnsureLiteralAttributeValue(element, attributeName, value);

        var trimmed = value.Trim();
        var index = 0;
        while (index < trimmed.Length)
        {
            var current = trimmed[index];
            if (char.IsDigit(current)
                || current == '+'
                || current == '-'
                || current == '.'
                || current == 'e'
                || current == 'E')
            {
                index++;
                continue;
            }

            break;
        }

        var numericPart = trimmed.Substring(0, index);
        var unitPart = trimmed.Substring(index);
        if (float.TryParse(numericPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericValue))
        {
            return (numericValue, unitPart);
        }

        throw new FormatException($"Invalid numeric value '{value}' for android:{attributeName} on <{element.Name.LocalName}>.");
    }

    private static bool MoveToRootElement(XmlReader reader)
    {
        return reader.MoveToContent() == XmlNodeType.Element;
    }

    private static bool IsVectorDrawable(string localName, string? androidNamespace)
    {
        return localName == "vector" && androidNamespace == AndroidNamespace;
    }

    private static string? GetNamespace(XElement element, string prefix)
    {
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration && attribute.Name.LocalName == prefix)
            {
                return attribute.Value;
            }
        }

        return null;
    }

    private static bool TryGetAndroidAttribute(XElement element, string attributeName, out string? value)
    {
        var attribute = element.Attribute(XName.Get(attributeName, AndroidNamespace));
        value = attribute?.Value;
        return attribute is not null;
    }

    private static string? GetOptionalAndroidAttribute(XElement element, string attributeName)
    {
        return TryGetAndroidAttribute(element, attributeName, out var value) ? value : null;
    }

    private static float CombineOpacity(float explicitOpacity, float colorOpacity)
    {
        return Clamp01(explicitOpacity * colorOpacity);
    }

    private static float DegreesToRadians(float degrees) => (float)(degrees * (Math.PI / 180.0));

    private static float Clamp01(float value)
    {
        if (value < 0f)
        {
            return 0f;
        }

        if (value > 1f)
        {
            return 1f;
        }

        return value;
    }

    private static void EnsurePositiveVectorDimension(string attributeName, SvgUnit value)
    {
        if (value.Value <= 0f)
        {
            throw new FormatException($"<vector> tag requires {attributeName} > 0");
        }
    }

    private static void EnsurePositiveVectorFloat(string attributeName, float value)
    {
        if (value <= 0f)
        {
            throw new FormatException($"<vector> tag requires {attributeName} > 0");
        }
    }

    private static XmlReaderSettings CreateReaderSettings()
    {
        return new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };
    }

    private sealed class ConversionContext
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private SvgDefinitionList? _definitions;

        public SvgDefinitionList GetDefinitions(SvgDocument document)
        {
            if (_definitions is not null)
            {
                return _definitions;
            }

            _definitions = new SvgDefinitionList();
            if (document.Children.Count == 0)
            {
                document.Children.Add(_definitions);
            }
            else
            {
                document.Children.Insert(0, _definitions);
            }
            return _definitions;
        }

        public string EnsureUniqueId(string candidate, string prefix)
        {
            var sanitized = SanitizeId(candidate, prefix);
            var unique = sanitized;
            var suffix = 1;
            while (_ids.Contains(unique))
            {
                unique = $"{sanitized}_{suffix++}";
            }

            _ids.Add(unique);
            return unique;
        }

        private static string SanitizeId(string candidate, string prefix)
        {
            var source = string.IsNullOrWhiteSpace(candidate) ? prefix : candidate.Trim();
            var builder = new StringBuilder(source.Length);
            for (var i = 0; i < source.Length; i++)
            {
                var current = source[i];
                if (char.IsLetterOrDigit(current) || current is '_' or '-' or '.')
                {
                    builder.Append(current);
                }
                else
                {
                    builder.Append('_');
                }
            }

            if (builder.Length == 0)
            {
                builder.Append(prefix);
            }

            if (char.IsDigit(builder[0]))
            {
                builder.Insert(0, $"{prefix}_");
            }

            return builder.ToString();
        }
    }
}
