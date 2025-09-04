const fs = require('fs');
const path = require('path');
const { getProjectRootDir } = require('./utils');

const CONFIG_FILE_NAME = 'winsdk.yaml';

// Simple YAML parser for our basic use case
function parseYaml(yamlString) {
  const lines = yamlString.split('\n');
  const result = { packages: [] };
  let currentPackage = null;
  
  for (const line of lines) {
    const trimmed = line.trim();
    
    if (trimmed === '' || trimmed.startsWith('#')) {
      continue; // Skip empty lines and comments
    }
    
    if (trimmed === 'packages:') {
      continue; // Skip the packages header
    }
    
    if (trimmed.startsWith('- name:')) {
      // Start of a new package
      currentPackage = {};
      const name = trimmed.replace('- name:', '').trim().replace(/['"]/g, '');
      currentPackage.name = name;
    } else if (trimmed.startsWith('version:') && currentPackage) {
      const version = trimmed.replace('version:', '').trim().replace(/['"]/g, '');
      currentPackage.version = version;
      result.packages.push(currentPackage);
      currentPackage = null;
    }
  }
  
  return result;
}

// Simple YAML serializer for our basic use case
function stringifyYaml(obj) {
  let yaml = 'packages:\n';
  
  for (const pkg of obj.packages) {
    yaml += `  - name: ${pkg.name}\n`;
    yaml += `    version: ${pkg.version}\n`;
  }
  
  return yaml;
}

/**
 * Default configuration structure
 */
const DEFAULT_CONFIG = {
  packages: []
};

/**
 * Get the path to the configuration file
 * @param {string} [projectRoot] - Project root directory (will be auto-detected if not provided)
 * @returns {string} - Absolute path to the config file
 */
function getConfigPath(projectRoot = null) {
  if (!projectRoot) {
    projectRoot = getProjectRootDir();
  }
  return path.join(projectRoot, CONFIG_FILE_NAME);
}

/**
 * Load configuration from file, creating default if it doesn't exist
 * @param {string} [projectRoot] - Project root directory (will be auto-detected if not provided)
 * @returns {Object} - Configuration object
 */
function loadConfig(projectRoot = null) {
  const configPath = getConfigPath(projectRoot);
  
  if (fs.existsSync(configPath)) {
    try {
      const configContent = fs.readFileSync(configPath, 'utf8');
      const config = parseYaml(configContent);
      
      // Merge with defaults to ensure all required properties exist
      return {
        ...DEFAULT_CONFIG,
        ...config
      };
    } catch (error) {
      throw new Error(`Failed to parse configuration file ${configPath}: ${error.message}`);
    }
  }
  
  // Return default config if file doesn't exist
  return { ...DEFAULT_CONFIG };
}

/**
 * Save configuration to file
 * @param {Object} config - Configuration object to save
 * @param {string} [projectRoot] - Project root directory (will be auto-detected if not provided)
 * @returns {string} - Path where config was saved
 */
function saveConfig(config, projectRoot = null) {
  const configPath = getConfigPath(projectRoot);
  
  try {
    const configContent = stringifyYaml(config);
    fs.writeFileSync(configPath, configContent, 'utf8');
    return configPath;
  } catch (error) {
    throw new Error(`Failed to save configuration file ${configPath}: ${error.message}`);
  }
}

/**
 * Get the version for a specific package from config
 * @param {Object} config - Configuration object
 * @param {string} packageName - Name of the package to get version for
 * @returns {string|null} - Version string or null if not specified
 */
function getPackageVersion(config, packageName) {
  const packageEntry = config.packages.find(pkg => pkg.name === packageName);
  return packageEntry ? packageEntry.version : null;
}

/**
 * Update or add a package version in the config
 * @param {Object} config - Configuration object to modify
 * @param {string} packageName - Name of the package
 * @param {string} version - Version to set
 * @returns {Object} - Modified configuration object
 */
function setPackageVersion(config, packageName, version) {
  const existingIndex = config.packages.findIndex(pkg => pkg.name === packageName);
  
  if (existingIndex >= 0) {
    config.packages[existingIndex].version = version;
  } else {
    config.packages.push({
      name: packageName,
      version: version
    });
  }
  
  return config;
}

/**
 * Check if config file exists
 * @param {string} [projectRoot] - Project root directory (will be auto-detected if not provided)
 * @returns {boolean} - True if config file exists
 */
function configExists(projectRoot = null) {
  const configPath = getConfigPath(projectRoot);
  return fs.existsSync(configPath);
}

/**
 * Create a new config file with the given packages and their versions
 * @param {Array} packages - Array of {name, version} objects
 * @param {string} [projectRoot] - Project root directory (will be auto-detected if not provided)
 * @returns {Object} - The created configuration object
 */
function createConfigWithPackages(packages, projectRoot = null) {
  const config = {
    ...DEFAULT_CONFIG,
    packages: packages.map(pkg => ({
      name: pkg.name,
      version: pkg.version
    }))
  };
  
  saveConfig(config, projectRoot);
  return config;
}

/**
 * Update config with new package versions after download
 * @param {Object} downloadResults - Results from package downloads
 * @param {string} [projectRoot] - Project root directory (will be auto-detected if not provided)
 * @returns {Object} - Updated configuration object
 */
function updateConfigWithDownloadResults(downloadResults, projectRoot = null) {
  const config = loadConfig(projectRoot);
  
  // Update package versions based on download results
  Object.entries(downloadResults).forEach(([packageName, result]) => {
    if (result && typeof result === 'object' && result.version) {
      // Handle both downloaded packages and existing packages
      setPackageVersion(config, packageName, result.version);
    } else if (result && typeof result === 'string' && result !== 'error') {
      // Legacy format - result is the version string directly
      setPackageVersion(config, packageName, result);
    }
  });
  
  saveConfig(config, projectRoot);
  return config;
}

module.exports = {
  CONFIG_FILE_NAME,
  DEFAULT_CONFIG,
  getConfigPath,
  loadConfig,
  saveConfig,
  getPackageVersion,
  setPackageVersion,
  configExists,
  createConfigWithPackages,
  updateConfigWithDownloadResults
};
