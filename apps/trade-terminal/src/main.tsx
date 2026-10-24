import React from 'react';
import { createRoot } from 'react-dom/client';
import Terminal from './Terminal';
import './terminal.css';

createRoot(document.getElementById('root')!).render(
  <React.StrictMode><Terminal /></React.StrictMode>
);
