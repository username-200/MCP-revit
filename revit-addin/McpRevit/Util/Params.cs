using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using McpRevit.Commands;
using Newtonsoft.Json.Linq;

namespace McpRevit.Util
{
    /// <summary>Типизированный доступ к параметрам команды с внятными сообщениями об ошибках.</summary>
    public static class Params
    {
        public static string String(JObject p, string name)
        {
            var value = (string)p[name];
            if (string.IsNullOrWhiteSpace(value))
                throw new CommandException("Не задан обязательный строковый параметр '" + name + "'.");
            return value;
        }

        public static string StringOr(JObject p, string name, string fallback)
        {
            var value = (string)p[name];
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        public static double Double(JObject p, string name)
        {
            var token = p[name];
            if (token == null || token.Type == JTokenType.Null)
                throw new CommandException("Не задан обязательный числовой параметр '" + name + "'.");
            return (double)token;
        }

        public static double DoubleOr(JObject p, string name, double fallback)
        {
            var token = p[name];
            return token == null || token.Type == JTokenType.Null ? fallback : (double)token;
        }

        public static int IntOr(JObject p, string name, int fallback)
        {
            var token = p[name];
            return token == null || token.Type == JTokenType.Null ? fallback : (int)token;
        }

        public static bool BoolOr(JObject p, string name, bool fallback)
        {
            var token = p[name];
            return token == null || token.Type == JTokenType.Null ? fallback : (bool)token;
        }

        public static long Id(JObject p, string name)
        {
            var token = p[name];
            if (token == null || token.Type == JTokenType.Null)
                throw new CommandException("Не задан обязательный идентификатор '" + name + "'.");
            return (long)token;
        }

        public static long IdOr(JObject p, string name, long fallback)
        {
            var token = p[name];
            return token == null || token.Type == JTokenType.Null ? fallback : (long)token;
        }

        public static JArray Array(JObject p, string name)
        {
            if (!(p[name] is JArray array))
                throw new CommandException("Параметр '" + name + "' должен быть массивом.");
            return array;
        }

        public static JArray ArrayOrEmpty(JObject p, string name) => p[name] as JArray ?? new JArray();

        /// <summary>Точка вида {"x": 0, "y": 0, "z": 0} в миллиметрах.</summary>
        public static XYZ Point(JObject p, string name)
        {
            if (!(p[name] is JObject point))
                throw new CommandException("Параметр '" + name + "' должен быть объектом {x, y, z} в мм.");
            return PointFrom(point, name);
        }

        public static XYZ PointFrom(JObject point, string context)
        {
            var x = point["x"];
            var y = point["y"];
            if (x == null || y == null)
                throw new CommandException("В точке '" + context + "' обязательны поля x и y (мм).");

            return UnitConv.PointFromMm((double)x, (double)y, (double)(point["z"] ?? 0.0));
        }

        /// <summary>Вектор вида {"x": 0, "y": 0, "z": 1}; единицы не важны, вектор нормируется.</summary>
        public static XYZ Direction(JObject p, string name)
        {
            if (!(p[name] is JObject vector))
                throw new CommandException("Параметр '" + name + "' должен быть объектом {x, y, z}.");

            var result = new XYZ(
                (double)(vector["x"] ?? 0.0),
                (double)(vector["y"] ?? 0.0),
                (double)(vector["z"] ?? 0.0));

            if (result.IsZeroLength())
                throw new CommandException("Вектор '" + name + "' нулевой длины.");

            return result.Normalize();
        }

        public static List<long> IdList(JObject p, string name)
        {
            var result = new List<long>();
            foreach (var token in Array(p, name))
                result.Add((long)token);
            return result;
        }

        /// <summary>Достаёт элемент по id и приводит к нужному типу.</summary>
        public static T Element<T>(Document doc, JObject p, string name) where T : Element
        {
            var id = Id(p, name);
            var element = doc.GetElement(RevitIds.FromLong(id));
            if (element == null)
                throw CommandException.NotFound("Элемент " + id);

            if (!(element is T typed))
                throw new CommandException(
                    "Элемент " + id + " имеет тип " + element.GetType().Name +
                    ", а ожидался " + typeof(T).Name + ".");

            return typed;
        }

        public static Document Document(Autodesk.Revit.UI.UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                throw new CommandException("В Revit не открыт ни один документ.", "no_document");
            if (doc.IsFamilyDocument)
                throw new CommandException(
                    "Активен документ семейства. Откройте проект (.rvt).", "wrong_document");
            return doc;
        }
    }
}
