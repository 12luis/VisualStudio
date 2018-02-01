﻿using System.Collections.Generic;
using GitHub.Services;
using NUnit.Framework;
using NSubstitute;

public class NavigationServiceTests
{
    public class TheFindNearestMatchingLineMethod
    {
        [TestCase(new[] { "line" }, new[] { "line" }, 0, 0, 1, Description = "Match same line")]
        [TestCase(new[] { "line" }, new[] { "line_no_match" }, 0, -1, 0, Description = "No matching line")]
        [TestCase(new[] { "line" }, new[] { "", "line" }, 0, 1, 1, Description = "Match line moved up")]
        [TestCase(new[] { "", "line" }, new[] { "line" }, 1, 0, 1, Description = "Match line moved down")]
        [TestCase(new[] { "line", "line" }, new[] { "line", "line" }, 0, 0, 2, Description = "Match nearest line")]
        [TestCase(new[] { "line", "line" }, new[] { "line", "line" }, 1, 1, 2, Description = "Match nearest line")]
        [TestCase(new[] { "line" }, new[] { "line" }, 1, 0, 1, Description = "Treat after last line the same as last line")]
        public void FindNearestMatchingLine(IList<string> fromLines, IList<string> toLines, int line,
            int expectNearestLine, int expectMatchingLines)
        {
            var target = CreateNavigationService();

            int matchedLines;
            var nearestLine = target.FindNearestMatchingLine(fromLines, toLines, line, out matchedLines);

            Assert.That(nearestLine, Is.EqualTo(expectNearestLine));
            Assert.That(matchedLines, Is.EqualTo(expectMatchingLines));
        }
    }

    public class TheFindMatchingLineMethod
    {
        [TestCase(new[] { "void method()", "code" }, new[] { "void method()", "// code" }, 1, 1, Description = "Find using line below")]
        [TestCase(new[] { "void method()", "code" }, new[] { "void method()", "// code", "// code" }, 2, -1, Description = "Out of range")]
        [TestCase(new[] { "code" }, new[] { "// code" }, 0, -1, Description = "No matching line")]
        [TestCase(new[] { "void method()", "code" }, new[] { "void method()" }, 1, 0, Description = "Keep within bounds")]
        [TestCase(new[] { "line", "line" }, new[] { "line", "line" }, 0, 0, Description = "Match nearest line")]
        [TestCase(new[] { "line", "line" }, new[] { "line", "line" }, 1, 1, Description = "Match nearest line")]
        public void FindNearestMatchingLine(IList<string> fromLines, IList<string> toLines, int line,
            int matchingLine)
        {
            var target = CreateNavigationService();

            var nearestLine = target.FindMatchingLine(fromLines, toLines, line, matchLinesAbove: 1);

            Assert.That(nearestLine, Is.EqualTo(matchingLine));
        }
    }

    static NavigationService CreateNavigationService() => new NavigationService();
}
