# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from pathlib import Path
import sys

from setuptools import setup
from setuptools.command.bdist_wheel import bdist_wheel
from setuptools.command.install import install

sys.path.insert(0, str(Path(__file__).parent / "src"))
from winappcli.native import native_platform

NATIVE = Path(__file__).parent / "src" / "winappcli" / "_native" / "winapp.exe"


class NativeInstall(install):
    def finalize_options(self):
        super().finalize_options()
        if NATIVE.is_file():
            self.install_lib = self.install_platlib


class NativeWheel(bdist_wheel):
    def finalize_options(self):
        super().finalize_options()
        if NATIVE.is_file():
            self.root_is_pure = False
            self.plat_name = native_platform(NATIVE)

    def get_tag(self):
        if NATIVE.is_file():
            return "py3", "none", native_platform(NATIVE)
        return super().get_tag()

    def run(self):
        if not NATIVE.is_file():
            raise RuntimeError(
                "A distributable microsoft-winappcli wheel requires the native CLI publish directory. "
                "Run scripts\\package-python.ps1 after building the CLI. "
                "For source development, use pip install -e src\\winapp-python instead."
            )
        native_platform(NATIVE)
        super().run()


setup(cmdclass={"bdist_wheel": NativeWheel, "install": NativeInstall})
