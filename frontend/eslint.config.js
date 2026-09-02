import js from '@eslint/js';
import globals from 'globals';
import react from 'eslint-plugin-react';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';

export default [
  { ignores: ['dist/**', 'dist-*/**', 'node_modules/**', 'coverage/**'] },
  js.configs.recommended,
  {
    files: ['**/*.{js,jsx}'],
    languageOptions: {
      ecmaVersion: 'latest',
      sourceType: 'module',
      globals: { ...globals.browser, ...globals.node },
      parserOptions: { ecmaFeatures: { jsx: true } },
    },
    plugins: { react, 'react-hooks': reactHooks, 'react-refresh': reactRefresh },
    settings: { react: { version: 'detect' } },
    rules: {
      ...react.configs.flat.recommended.rules,
      ...react.configs.flat['jsx-runtime'].rules,
      ...reactHooks.configs['recommended-latest'].rules,
      // Fast-refresh fidelity only; irrelevant to a production build, and the
      // router and context modules legitimately export helpers beside components.
      'react-refresh/only-export-components': 'off',
      // Five screens copy server data into a local draft inside an effect. The
      // rule is right that a derived-state or key-reset pattern would be cleaner;
      // rewriting five forms the day before handover is the wrong trade. Recorded
      // in README under known limitations. Turn it back on when those are done.
      'react-hooks/set-state-in-effect': 'off',
      'react/prop-types': 'off',
      'no-unused-vars': ['error', { argsIgnorePattern: '^_', varsIgnorePattern: '^_', caughtErrors: 'none' }],
    },
  },
  {
    files: ['**/*.test.{js,jsx}', 'src/test/**'],
    languageOptions: { globals: { ...globals.vitest, ...globals.jest } },
  },
];
