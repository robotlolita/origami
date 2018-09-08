const { parse } = require("./parser");
const { compileModule, generate } = require("./codegen");
const babel = require("@babel/core");
const fs = require("fs");
const path = require("path");

function read(f) {
  return fs.readFileSync(f, "utf8");
}

const runtime = read(path.join(__dirname, "runtime.js"));

function compile(program) {
  const js = generate(parse(program)).code;
  return `${runtime}\n${js}`;
}

function compileToNode(program) {
  const jsAst = compileModule(parse(program));
  const js = babel.transformFromAstSync(jsAst, null, {
    plugins: ["@babel/plugin-transform-modules-commonjs"]
  });
  return `${runtime}\n${js.code}`;
}

function register() {
  require.extensions[".origami"] = (mod, file) => {
    const program = read(file);
    mod._compile(compileToNode(program), file);
  };
}

module.exports = {
  parse,
  compile,
  compileToNode,
  register
};