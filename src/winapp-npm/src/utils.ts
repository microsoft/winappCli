import * as fs from 'fs';
import * as path from 'path';

/**
 * Finds the nearest package.json file by traversing up the directory tree
 * and returns the absolute path of its parent folder (project root).
 *
 * @param startDir - The directory to start searching from (defaults to current working directory)
 * @returns The absolute path of the project root directory
 * @throws Error if no package.json is found
 */
export function getProjectRootDir(startDir: string = process.cwd()): string {
  // During npm install, we want to find the consumer project, not this package
  // npm sets various environment variables during installation
  if (process.env.npm_config_prefix && process.env.INIT_CWD) {
    // INIT_CWD is the directory where npm was initially run (consumer project)
    startDir = process.env.INIT_CWD;
  }

  let currentDir = path.resolve(startDir);

  while (true) {
    const packageJsonPath = path.join(currentDir, 'package.json');

    if (fs.existsSync(packageJsonPath)) {
      // During npm install, skip the @microsoft/winappcli package itself
      try {
        const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, 'utf8'));
        if (packageJson.name === '@microsoft/winappcli' && process.env.npm_config_prefix) {
          // This is the @microsoft/winappcli package itself, keep looking up
          const parentDir = path.dirname(currentDir);
          if (parentDir === currentDir) {
            throw new Error('No consumer package.json found in the directory tree');
          }
          currentDir = parentDir;
          continue;
        }
      } catch {
        // If we can't parse the package.json, still use it
      }

      return currentDir;
    }

    const parentDir = path.dirname(currentDir);

    // If we've reached the root directory (no parent), stop searching
    if (parentDir === currentDir) {
      throw new Error('No package.json found in the directory tree');
    }

    currentDir = parentDir;
  }
}
