/** @type {import('next').NextConfig} */

import newrelic from 'newrelic';
import path from 'path';

const __dirname = new URL('.', import.meta.url).pathname;

const nextConfig = {
  experimental: {
    // Without this setting, the Next.js compilation step will routinely
    // try to import files such as `LICENSE` from the `newrelic` module.
    // See https://nextjs.org/docs/app/api-reference/next-config-js/serverComponentsExternalPackages.
    serverComponentsExternalPackages: ['newrelic']
  },
  webpack: (config) => {
    config.resolve.alias = {
      ...config.resolve.alias,
      '@': path.resolve(__dirname, './'),
    };

    return config;
  },
};

// console.log('Starting thermal-printer-web');
// newrelic.incrementMetric('thermal-printer-web.start');
// console.log('Started thermal-printer-web');

export default nextConfig;
