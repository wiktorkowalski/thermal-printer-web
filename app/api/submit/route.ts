import { NextResponse } from "next/server";
import { BreakLine, CharacterSet, ThermalPrinter } from "node-thermal-printer";
import newrelic from 'newrelic';
import * as zod from "zod";

const printer = new ThermalPrinter({
  interface: 'tcp://192.168.123.100',
  characterSet: CharacterSet.PC852_LATIN2,
  removeSpecialCharacters: false,
  lineCharacter: "=",
  breakLine: BreakLine.WORD,
  options:{
    timeout: 5000,
  },
});

const formSchema = zod.object({
  name: zod.string().min(1).max(50),
  message: zod.string().min(1).max(5000),
});

export async function POST(req: Request) {
  try {
    const data = await req.json();

    const validatedData = formSchema.parse(data);

    if(validatedData.name === "error") {
      console.log("Error processing form, name is 'error'");
      newrelic.noticeError(new Error("Error processing form"));
    }

    console.log("Form submitted:", validatedData);

    console.log(await printer.isPrinterConnected());
    printer.clear();
    printer.alignCenter();
    printer.bold(true);
    printer.setTextDoubleHeight();
    printer.setTextDoubleWidth();
    printer.drawLine();
    printer.println(validatedData.name);
    printer.drawLine();
    printer.println(validatedData.message);
    printer.drawLine();
    printer.cut();
    await printer.execute();
    printer.clear();

    return NextResponse.json({ message: "Form submitted successfully", validatedData });
  } catch (error) {
    console.error("Error processing form:", error);
    // newrelic.noticeError(error as Error);
    return NextResponse.json({ message: "Error processing form", error }, { status: 500 });
  }
}
