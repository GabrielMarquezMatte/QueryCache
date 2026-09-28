#!/usr/bin/env python3
"""Unit tests for add_missing_public_api.py.

Run: python3 .github/scripts/test_add_missing_public_api.py
"""

import os
import tempfile
import unittest

import add_missing_public_api as add


class RouteTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.core = os.path.join(self._tmp.name, "src", "Core")
        self.arrow = os.path.join(self._tmp.name, "src", "Arrow")
        for d, name in ((self.core, "Core.csproj"), (self.arrow, "Arrow.csproj")):
            os.makedirs(d)
            open(os.path.join(d, name), "w").close()

    def tearDown(self):
        self._tmp.cleanup()

    def _line(self, symbol, project_dir, csproj):
        return (f"{project_dir}/X.cs(1,1): warning RS0016: Symbol '{symbol}' is not part of the "
                f"declared public API (https://example) [{os.path.join(project_dir, csproj)}]")

    def test_symbol_goes_to_the_project_that_reported_it(self):
        output = "\n".join([
            self._line("Core.Foo", self.core, "Core.csproj"),
            self._line("Arrow.Bar", self.arrow, "Arrow.csproj"),
        ])

        found = add.route_missing_symbols(output, [self.core, self.arrow])

        self.assertEqual(found, {self.core: {"Core.Foo"}, self.arrow: {"Arrow.Bar"}})

    def test_untracked_project_is_ignored(self):
        tests_dir = os.path.join(self._tmp.name, "tests", "T")
        output = self._line("T.Baz", tests_dir, "T.csproj")

        self.assertEqual(add.route_missing_symbols(output, [self.core]), {})

    def test_repeated_diagnostic_is_deduplicated(self):
        line = self._line("Core.Foo", self.core, "Core.csproj")

        found = add.route_missing_symbols(line + "\n" + line, [self.core])

        self.assertEqual(found, {self.core: {"Core.Foo"}})


if __name__ == "__main__":
    unittest.main()
