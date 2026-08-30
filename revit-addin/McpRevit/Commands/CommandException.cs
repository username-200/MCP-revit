using System;

namespace McpRevit.Commands
{
    /// <summary>
    /// Ошибка, которую осмысленно показать вызывающей стороне: неверные параметры,
    /// отсутствующий элемент, неподходящее состояние документа.
    /// </summary>
    public class CommandException : Exception
    {
        public string Kind { get; }

        public CommandException(string message, string kind = "bad_request") : base(message)
        {
            Kind = kind;
        }

        public static CommandException NotFound(string what) =>
            new CommandException(what + " не найден(о) в документе.", "not_found");
    }
}
